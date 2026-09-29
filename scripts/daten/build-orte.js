// Baut src/Zwijg.Core/Pseudonymization/Data/orte-alle.txt neu. Aus dem Hauptordner des Repos ausführen:
//
//   curl -LO https://download.geonames.org/export/dump/DE.zip && unzip DE.zip DE.txt
//   curl -LO https://raw.githubusercontent.com/hermitdave/FrequencyWords/master/content/2018/de/de_50k.txt
//   node scripts/daten/build-orte.js src/Zwijg.Core/Pseudonymization/Data DE.txt de_50k.txt
//
// GeoNames steht unter CC BY 4.0 (Namensnennung, siehe THIRD-PARTY-NOTICES.md).
// Die Wortliste dient nur zum Aussortieren von Orten, die auch häufige Wörter sind, und kommt nicht ins Repo.
// Ohne ~ wird der Ort immer erkannt, mit ~ nur mit Hinweis davor ("in", "aus", "Landkreis" ...).
const fs = require("fs");
const path = require("path");
const [dataDir, geonamesFile = "DE.txt", wordsFile = "de_50k.txt"] = process.argv.slice(2);

const readList = f => new Set(fs.readFileSync(path.join(dataDir, f), "utf8").split(/\r?\n/)
  .map(l => l.trim()).filter(l => l && !l.startsWith("#")).map(l => l.replace(/^~/, "")));
const firstNames = readList("vornamen.txt");
const surnames = readList("nachnamen.txt");
const curated = readList("orte.txt");

// Häufige deutsche Wörter, nur zum Filtern
const freq = new Map();
for (const line of fs.readFileSync(wordsFile, "utf8").split(/\r?\n/)) {
  const [w, n] = line.split(" ");
  if (w) freq.set(w.toLowerCase(), Number(n));
}

// Wie im PlaceDetector: nach "in", "aus" usw. keine Orte
const notAPlace = new Set(("Dingen Umfeld Vorfeld Rahmen Gebrauch Anfang Verlauf Bedarf Bereich Folge Richtung Abstand Tabletten " +
  "Tropfen Rücken Brücke Stadt Heim Pflegeheim Altenheim Kinderheim Krankenhaus Klinik Praxis Urlaub Ruhe Absprache Rücksprache " +
  "Behandlung Therapie Untersuchung Gegenwart Zukunft Vergangenheit Deutschland Europa Ausland Inland Bett Hause Arbeit Schule Kita " +
  "Kindergarten Notaufnahme Station Reha Kur Anbetracht Bezug Kombination Abhängigkeit Ordnung Berg Burg Dorf Feld Hafen Horn Leben " +
  "Kirchen Bach Hof Gefahr Schwangerschaft Zentrum Mitte Nord Süd Ost West Altstadt Neustadt Innenstadt Siedlung Mühle Kolonie").split(" "));

// Größere Orte, die auch ein normales Wort oder ein Name sind
const bigAmbiguous = new Set(("Essen Siegen Erlangen Gießen Mitte Wedding Hof Singen Kamen Weiden Langen Linden Horn Lage Leer Springe " +
  "Wetter Wangen Roth Norden Kalk Senden Waren Halle Heide Löhne Datteln Hagen Kiel Lehrte Frechen Geldern Konstanz Auerbach Dachau").split(" "));

const places = new Map(); // Name -> höchste Einwohnerzahl
const add = (name, pop) => {
  name = name.trim().replace(/\s+/g, " ");
  if (name.length < 3 || /[\d,/;"]/.test(name) || !/^\p{Lu}/u.test(name)) return;
  places.set(name, Math.max(places.get(name) ?? 0, pop));
};

const prefix = /^(?:Landkreis|Kreis|Stadt|Gemeinde|Markt|Hansestadt|Universitätsstadt|Samtgemeinde|Verbandsgemeinde|Amt|Städteregion|Regionalverband|Regierungsbezirk|Region)\s+/;
const keepCodes = new Set(["PPL", "PPLA", "PPLA2", "PPLA3", "PPLA4", "PPLC", "PPLX", "PPLS"]);

for (const line of fs.readFileSync(geonamesFile, "utf8").split("\n")) {
  const c = line.split("\t");
  if (c.length < 15) continue;
  const [name, fclass, fcode, pop] = [c[1], c[6], c[7], Number(c[14]) || 0];
  const wanted = (fclass === "P" && keepCodes.has(fcode))
    || (fclass === "A" && (fcode === "ADM3" || fcode === "ADM4"))
    || (fclass === "A" && fcode === "ADM2" && name.startsWith("Regierungsbezirk"));
  if (!wanted) continue;

  const names = [name];
  const paren = name.match(/^(.+?)\s*\((.+)\)$/); // "Halle (Saale)" auch als "Halle" und "Halle an der Saale"
  if (paren) names.push(paren[1]);
  for (const n of [...names]) if (prefix.test(n)) names.push(n.replace(prefix, ""));
  for (const n of names) add(n, pop);
}

let standalone = 0, context = 0, dropped = 0;
const out = [];
for (const [name, pop] of [...places].sort((a, b) => a[0].localeCompare(b[0], "de"))) {
  const single = !name.includes(" ");
  // Größere Orte werden von Hand beurteilt, weil die Häufigkeitsliste auch oft genannte Städte wie Berlin zählt
  const common = single && (pop >= 20000 ? bigAmbiguous.has(name) : (freq.get(name.toLowerCase()) ?? 0) >= 20);
  const person = firstNames.has(name) || surnames.has(name);

  if (curated.has(name)) continue; // die handgepflegte Liste hat Vorrang
  if (notAPlace.has(name) || (common && pop < 20000)) { dropped++; continue; }

  if (!common && !person && pop >= 10000) { out.push(name); standalone++; }
  else { out.push("~" + name); context++; }
}

const header = [
  "# Alle Gemeinden, Städte, Ortsteile und Landkreise in Deutschland.",
  "# Erzeugt aus GeoNames (https://www.geonames.org, CC BY 4.0). Nicht von Hand ändern, eigene Orte gehören in orte.txt.",
  "# Ohne ~ wird der Ort immer erkannt (ab 10.000 Einwohnern, kein normales Wort und kein Name).",
  "# Mit ~ nur mit Hinweis davor, zum Beispiel \"in\", \"aus\", \"bei\" oder \"Landkreis\".",
  "# Kleine Orte, die auch ein häufiges Wort sind (Bruch, Brand ...), fehlen bewusst. Die findet \"wohnt in ...\" trotzdem.",
];
fs.writeFileSync(path.join(dataDir, "orte-alle.txt"), header.concat(out).join("\n") + "\n");
console.log(`immer: ${standalone}, mit Hinweis: ${context}, weggelassen: ${dropped}, Datei: ${out.length} Zeilen`);
