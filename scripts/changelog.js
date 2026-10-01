// Baut den Abschnitt für eine neue Version in CHANGELOG.md aus den gemergten Pull Requests.
// Aufruf: node scripts/changelog.js 1.1.0 prs.json
// prs.json kommt von: gh pr list --state merged --base main --json number,title,labels,author,mergedAt --limit 500
const fs = require("fs");
const path = require("path");

// Reihenfolge der Abschnitte. Pull Requests mit "internal" tauchen nicht auf.
const SECTIONS = [
  ["security", "Sicherheit"],
  ["new", "Neu"],
  ["improvement", "Verbesserungen"],
  ["bug", "Fehler behoben"],
];

const [version, prsFile, since] = process.argv.slice(2);
if (!/^\d+\.\d+\.\d+$/.test(version || "")) {
  console.error("Die Version muss so aussehen: 1.0.0");
  process.exit(1);
}

const file = path.join(__dirname, "..", "CHANGELOG.md");
const changelog = fs.readFileSync(file, "utf8").replace(/\r\n/g, "\n");
if (new RegExp(`^## v?${version.replace(/\./g, "\\.")}\\b`, "m").test(changelog)) {
  console.error(`Version ${version} steht schon in CHANGELOG.md`);
  process.exit(1);
}

// Nur was seit der letzten Version gemergt wurde
const prs = JSON.parse(fs.readFileSync(prsFile, "utf8"))
  .filter(pr => !since || new Date(pr.mergedAt) > new Date(since))
  .sort((a, b) => a.number - b.number);

const labelsOf = pr => pr.labels.map(l => l.name);
const line = pr => `- ${pr.title} (#${pr.number}${pr.author?.login && !pr.author.is_bot ? `, @${pr.author.login}` : ""})`;

// Jeder Pull Request kommt in genau einen Abschnitt, bei mehreren Labels in den wichtigsten
const sectionOf = pr => {
  const labels = labelsOf(pr);
  const hit = SECTIONS.find(([label]) => labels.includes(label));
  return hit ? hit[1] : labels.includes("internal") ? null : "Sonstiges";
};

const parts = [];
for (const heading of [...SECTIONS.map(s => s[1]), "Sonstiges"]) {
  const items = prs.filter(pr => sectionOf(pr) === heading);
  if (items.length) parts.push(`### ${heading}\n\n${items.map(line).join("\n")}`);
}

if (!parts.length) parts.push("### Verbesserungen\n\n- Kleinere Verbesserungen");

const today = new Date().toISOString().slice(0, 10);
const section = `## ${version} (${today})\n\n${parts.join("\n\n")}\n`;

// Vor der ersten Version einfügen, sonst ans Ende
const first = changelog.search(/^## /m);
const updated = first < 0
  ? changelog.trimEnd() + "\n\n" + section
  : changelog.slice(0, first) + section + "\n" + changelog.slice(first);

fs.writeFileSync(file, updated);
console.log(section);
