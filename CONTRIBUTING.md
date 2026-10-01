# Mitmachen

Danke, dass du Zwijg verbessern willst. Fehlermeldungen, Ideen und Pull Requests sind willkommen.

## Wichtig: keine echten Patientendaten

Bitte nie echte Namen, Geburtsdaten, Versichertennummern oder Befunde in Issues, Pull Requests, Tests oder Screenshots verwenden. Nur ausgedachte Beispiele wie "Max Mustermann".

## So geht es

1. Repository forken und einen eigenen Branch anlegen
2. Änderung machen, am besten mit Test
3. `dotnet test` muss grün sein
4. Pull Request öffnen und kurz beschreiben, was und warum
5. Ein Label vergeben, damit die Änderung im Changelog richtig einsortiert wird: `security` (Lücke geschlossen), `new` (neue Funktion), `improvement` (Bestehendes besser), `bug` (Fehler behoben) oder `internal` (Tests, Doku, Aufräumen, taucht im Changelog nicht auf). Der Titel des Pull Requests landet so im Changelog, also bitte verständlich formulieren.

Kommentare und Texte in der Oberfläche sind auf Deutsch, schlicht und kurz.

Updates müssen alte Installationen weiter laden können:

- Neue Felder in `settings.json` bekommen einen sinnvollen Standardwert. Muss sich Bestehendes ändern, kommt ein neuer Schritt in `SettingsStore.Migrations`.
- Neue Spalten oder Tabellen in den Datenbanken kommen als neuer Schritt in `SqliteSchema.Migrate` dazu. Alte Schritte werden nie geändert.
- Die Endpunkte unter `/v1` bleiben kompatibel. Felder dürfen dazukommen, aber nicht wegfallen.

## Neue Version veröffentlichen

Nur für den Maintainer.

1. **Actions**, **Changelog vorbereiten**, **Run workflow**, Version eingeben, zum Beispiel `1.1.0`
2. Im Ergebnis auf **Pull Request öffnen** klicken, den Text in `CHANGELOG.md` für Praxen verständlich formulieren und mergen
3. **Actions**, **Release**, **Run workflow**, dieselbe Version eingeben

Der Release Text kommt aus `CHANGELOG.md`. Steht dort ein Abschnitt **Sicherheit**, zeigt Zwijg das Update als Sicherheitsupdate an.

## Rechte an Beiträgen

Zwijg steht unter der GNU AGPL 3.0 mit den Zusatzbedingungen aus `NOTICE`. Damit das Projekt auch später frei entscheiden kann, wie es weitergeht (zum Beispiel eine neue Lizenzversion oder eine zusätzliche Lizenz für Firmen), gilt für jeden Beitrag Folgendes.

Mit dem Einreichen eines Beitrags (Pull Request, Patch, Code in einem Issue oder ähnliches) erklärst du:

1. **Der Beitrag stammt von dir** oder du hast das Recht, ihn einzureichen. Gehört er rechtlich deinem Arbeitgeber, hast du dessen Erlaubnis.
2. **Du räumst Kay Schnieders ein Nutzungsrecht ein.** Es ist einfach (nicht ausschließlich), unwiderruflich, unentgeltlich und zeitlich, räumlich und inhaltlich unbeschränkt. Es umfasst das Recht, den Beitrag zu vervielfältigen, zu verändern, zu verbreiten, öffentlich zugänglich zu machen und Unterlizenzen zu vergeben, auch unter anderen Lizenzen als der AGPL, einschließlich kommerzieller Lizenzen.
3. **Patente:** Soweit du Patente hältst, die dein Beitrag nutzt, räumst du dafür im selben Umfang eine Lizenz ein.
4. **Du behältst dein Urheberrecht.** Du darfst deinen Beitrag weiterhin selbst nutzen, wie du willst.
5. **Im Projekt wird dein Beitrag unter der AGPL 3.0** mit den Bedingungen aus `NOTICE` veröffentlicht. Genannt wirst du in der Git Historie.
6. Es besteht **kein Anspruch**, dass ein Beitrag aufgenommen wird, und keine Pflicht, ihn zu pflegen.
7. Du stellst den Beitrag **ohne Gewähr** bereit.

Bist du damit nicht einverstanden, reiche bitte keinen Code ein. Ideen und Fehlermeldungen als Issue sind trotzdem willkommen.

## Contributor terms (English)

By submitting a contribution (pull request, patch, code in an issue or similar) you confirm that:

1. The contribution is your own work, or you have the right to submit it. If your employer holds rights to it, you have their permission.
2. You grant Kay Schnieders a non-exclusive, irrevocable, royalty-free, worldwide and perpetual license to use, reproduce, modify, distribute, make publicly available and sublicense your contribution, including under licenses other than the AGPL and including commercial licenses.
3. If you hold patents used by your contribution, you grant a patent license of the same scope.
4. You keep your copyright and may use your contribution in any way you like.
5. Within this project your contribution is published under the GNU AGPL 3.0 with the additional terms in `NOTICE`.
6. There is no right to have a contribution accepted and no obligation to maintain it.
7. You provide your contribution without warranty.

If the German and English versions differ, the German version applies.
