using System.Text.RegularExpressions;
using Xunit.Abstractions;
using Zwijg.Core.Pseudonymization;

namespace Zwijg.Tests;

// Prüfkatalog für die Erkennung ohne lokales Modell. Alle Namen sind ausgedacht.
// [[...]] muss als Name ersetzt werden, {{...}} als Ort. In den Sätzen ohne Markierung darf nichts ersetzt werden.
public partial class DetectionBenchmarkTests(ITestOutputHelper output)
{
    public static readonly string[] WithPeople =
    [
        // Anrede und Titel
        "Herr [[Yilmaz]] hat heute angerufen.",
        "Frau Dr. [[Nowak-Kowalczyk]] bittet um Rückruf.",
        "Rückfrage bei Frau [[Gül]] [[Demir]] wegen der Überweisung.",
        "Herrn [[Björn]] [[O'Neill]] bitte das Rezept mitgeben.",
        "Termin für Frau [[Zoë]] [[Albers]] verschieben.",
        "Herr und Frau [[Becker]] kommen zusammen zur Impfung.",
        "Die Eheleute [[Hartung]] haben die Unterlagen gebracht.",
        "Ehepaar [[Grünwald]] wartet im Flur.",
        "frau [[schmidt]] hat angerufen, bitte zurückrufen.",
        "herr [[yilmaz]] kommt morgen später.",
        // Vorname und Nachname ohne Anrede
        "[[Kübra]] [[Aydın]] hat seit gestern Fieber.",
        "[[Svetlana]] [[Iwanowa]] braucht eine AU bis Freitag.",
        "[[Mehmet]] [[Öztürk]] wurde gestern entlassen.",
        "[[Aleksandra]] [[Wiśniewska]] fragt nach ihrem Laborwert.",
        "[[Ahmad]] [[Al-Hassan]] kommt zur Nachkontrolle.",
        "[[Jean-Luc]] [[Dupont]] hat den Termin abgesagt.",
        "[[Nguyen]] [[Thi]] [[Lan]] bekommt die zweite Impfung.",
        "[[Jürgen]] [[Weißenborn]] klagt über Schwindel.",
        "[[Fatima]] [[Benali]] ist schwanger, 12. Woche.",
        "[[Dragan]] [[Petrović]] hat Rückenschmerzen.",
        "[[Oksana]] [[Kowalenko]] braucht einen Dolmetscher.",
        "[[Giuseppe]] [[Esposito]] nimmt Ramipril 5 mg.",
        "[[Kostas]] [[Papadopoulos]] hat einen Termin um 10 Uhr.",
        "[[Hanne]] [[Brüggemann]] leidet an Morbus Parkinson.",
        "[[Wiebke]] [[Oltmanns]] ist die neue Patientin.",
        // Nachname vorne, Formulare, Listen
        "[[MÜLLER]], [[Petra]], geb. 03.04.1961",
        "Name des Patienten: [[Jonas]] [[Brandt]]",
        "Patienten: [[Meyer]], [[Schulze]] und [[Nowak]]",
        "8:00 [[Yildirim]], 8:30 [[Kaminski]], 9:00 [[Hoffmann]]",
        "Heute um 10:15 Uhr [[Brinkhoff]], danach Pause.",
        "Pat.: [[Wendt]], [[Carola]]",
        "Versicherte: [[Dilara]] [[Kaya]]",
        "Frau [[Lehmann]], geb. [[Schuster]], wohnt in {{Lingen}}.",
        "Frau [[Anna]] [[Weber]] geborene [[Fischer]] kommt heute.",
        // Familie und Umfeld
        "Die Tochter [[Mia]] begleitet sie.",
        "Ehemann [[Detlef]] ruft an, wenn es schlechter wird.",
        "Ihr Sohn [[Emre]] übersetzt beim Termin.",
        "Betreuerin [[Sabine]] [[Wolters]] hat die Vollmacht.",
        // Später nur noch der Nachname, Besitzform
        "Herr [[Stegemann]] war da. [[Stegemann]] braucht neue Tabletten.",
        "Frau [[Lindqvist]] hat angerufen. [[Lindqvists]] Blutdruck ist zu hoch.",
        "[[Tobias]] [[Rieger]] hat Diabetes. Bei [[Rieger]] HbA1c kontrollieren.",
        // Signatur und Kollegen
        "Mit freundlichen Grüßen\n[[Katrin]] [[Albers]]",
        "Befund bitte an Dr. med. [[Hans-Peter]] [[Klein]] faxen.",
        "Rücksprache mit Kollegin Dr. [[Ferreira]] erfolgt.",
        "Überweisung von Dr. [[Uwe]] [[Harms]] liegt vor.",
        // Orte
        "Sie wohnt in {{Nordhorn}} und arbeitet in {{Lingen}}.",
        "Patient kommt aus {{Schüttorf}}.",
        "Adresse: Hauptstraße 5, 48529 {{Nordhorn}}",
        "Umgezogen nach {{Bad Bentheim}}.",
        "Geburtsort: {{Uelsen}}",
        "Er wohnt in {{Emlichheim}} bei seiner Tochter.",
        "Wohnhaft in {{Wietmarschen}}, Nähe {{Lohne}}.",
    ];

    public static readonly string[] WithoutPeople =
    [
        "Morbus Parkinson, Morbus Crohn und Hashimoto Thyreoiditis sind bekannt.",
        "Verdacht auf Alzheimer Demenz, Kontrolle in drei Monaten.",
        "Down Syndrom, Asperger Syndrom und Tourette sind Diagnosen, keine Namen.",
        "Positives Babinski Zeichen links, Lasègue negativ.",
        "Glasgow Coma Scale 15, Apgar 9 nach fünf Minuten.",
        "Basedow Krankheit, Cushing Syndrom und Addison Krise ausgeschlossen.",
        "Ramipril 5 mg, Bisoprolol 2,5 mg und Metformin 1000 mg täglich.",
        "Ibuprofen 400 bei Bedarf, maximal dreimal täglich.",
        "Die Patientin hat seit Montag Kopfschmerzen und Übelkeit.",
        "Der Patient klagt über Schwindel beim Aufstehen.",
        "Blutdruck 150/95, Puls 88, Temperatur 38,2.",
        "Termin im April oder Mai, am besten Dienstag früh.",
        "Bitte Überweisung an die Kardiologie schreiben.",
        "Laborwerte: HbA1c 7,2 Prozent, Kreatinin 1,1, TSH normal.",
        "Die Wunde heilt gut, Fäden am Freitag ziehen.",
        "Impfung gegen Grippe und Corona gleichzeitig möglich.",
        "Kind hat Fieber seit zwei Tagen, trinkt aber gut.",
        "Sie möchte eine Zweitmeinung einholen.",
        "Er war lange Zeit im Ausland und kommt jetzt zurück.",
        "Klein und schwach wirkte der Patient heute.",
        "Braun verfärbte Stelle am Unterschenkel, bitte Foto machen.",
        "Lange Wartezeiten sind ärgerlich, bitte entschuldigen.",
        "Sommer und Winter haben unterschiedliche Allergene.",
        "Der Koch in der Reha hat auf glutenfrei umgestellt.",
        "Wolf im Gesicht, also Intertrigo, gut behandelbar.",
        "Die Schwester auf Station hat den Verband gewechselt.",
        "Frau hat Schmerzen im Knie seit dem Sturz.",
        "Herr klagt über Husten.",
        "Hausbesuch am Nachmittag geplant.",
        "Ein Stein in der Niere wurde im Ultraschall gesehen.",
        "Der Fuchsbandwurm wurde ausgeschlossen.",
        "Kontrolle beim Hautarzt, Muttermal beobachten.",
        "Keine Allergien bekannt, Impfstatus vollständig.",
        "Tabletten morgens mit viel Wasser einnehmen.",
        "Die Krankenkasse hat den Antrag bewilligt.",
        "Medikamentenplan wurde aktualisiert und ausgedruckt.",
        "Guten Tag, ich brauche einen Termin für nächste Woche.",
        "Vielen Dank für die schnelle Hilfe.",
        "Das Rezept liegt vorne am Empfang bereit.",
        "Wir bitten um Mitbeurteilung und gegebenenfalls ein Röntgen.",
        "7:30 Aufstehen, 8:00 Frühstück, 12:30 Mittagessen, 22:00 Schlafen.",
        "Um 20:00 Tabletten nehmen, um 8:00 Blutdruck messen.",
        "Ab 14:00 Uhr Sprechstunde nur nach Vereinbarung.",
        "Heute 9:15 Ultraschall, 10:00 Befundbesprechung, 11:30 Akupunktur.",
    ];

    // Zweiter Katalog, erst nach den Verbesserungen geschrieben und nicht zum Anpassen der Regeln benutzt
    public static readonly string[] Unseen =
    [
        "Frau [[Tatjana]] [[Schewtschuk]] hat den Termin um 11 Uhr bestätigt.",
        "Bitte Herrn [[Kaczmarczyk]] wegen der Blutwerte anrufen.",
        "[[Lukas]] [[Wiechmann]] (8 Jahre) hat Mittelohrentzündung.",
        "Der Vater, [[Rüdiger]] [[Haverkamp]], holt das Rezept ab.",
        "[[Esra]] [[Günaydın]] fragt, ob die Überweisung fertig ist.",
        "Rückruf an [[Magdalena]] [[Szczepaniak]] bis 16 Uhr.",
        "Laborbefund von [[Heinz-Dieter]] [[Vosskötter]] ist da.",
        "14:00 [[Terwey]], 14:20 [[Olthuis]], 14:40 [[Kruse]]",
        "Frau [[Egberdina]] [[Hindriks]] kommt mit ihrer Tochter [[Janna]].",
        "Herr [[Van den Berg]] hat die Krankmeldung verlängert.",
        "Unterlagen für Eheleute [[Rottmann]] liegen bereit.",
        "[[Yusuf]] [[Karataş]] hat Blut im Urin, bitte zeitnah einbestellen.",
        "Name: [[Möhlmann]], [[Gisela]]",
        "Die Mutter von [[Lea]] hat angerufen, [[Lea]] hat wieder Fieber.",
        "Termin mit Herrn [[Abdulrahman]] [[Mousa]] und Dolmetscher.",
        "Frau [[Wübbels]] geb. [[Snoek]] braucht eine neue Versichertenkarte.",
        "[[Olena]] [[Hryhorenko]] braucht die Impfbescheinigung auf Englisch.",
        "Pflegedienst meldet: Herr [[Deters]] ist gestürzt.",
        "Befund an Hausärztin Dr. [[Menke-Gerdes]] schicken.",
        "Patientin [[Swaantje]] [[Ubbens]], 34 Jahre, Kontrolle in vier Wochen.",
        "Sie wohnt jetzt in {{Neuenhaus}}.",
        "Er kommt aus {{Wilsum}} und arbeitet in {{Nordhorn}}.",
        "Rezept für Frau [[Kamphuis]] liegt am Empfang.",
        "Herr [[Nieborg]] und Frau [[Nieborg]] kommen gemeinsam.",
        "[[Chiara]] [[Lombardo]] möchte eine Zweitmeinung.",
    ];

    public static readonly string[] UnseenWithoutPeople =
    [
        "Hashimoto und Basedow sind beides Schilddrüsenerkrankungen.",
        "Nach der Impfung 15 Minuten im Wartezimmer bleiben.",
        "Der Blutzucker lag heute morgen bei 180.",
        "Kinder unter sechs Jahren bekommen den Saft.",
        "Der Kaiserschnitt verlief ohne Komplikationen.",
        "Sie hat seit Weihnachten Probleme mit dem Magen.",
        "Braune Flecken am Rücken, bitte dermatologisch abklären.",
        "Klein anfangen und die Dosis langsam steigern.",
        "Kurz vor dem Essen einnehmen.",
        "Ab Montag gilt die neue Sprechstundenzeit.",
        "Morgens Pantoprazol, abends Simvastatin.",
        "Der Patient ist Rechtshänder und arbeitet als Maurer.",
        "Termin 8:00 Blutabnahme, 8:30 Impfung, 9:00 EKG.",
        "Ein Richter hat die Betreuung angeordnet.",
        "Frau klagt über Schmerzen im Handgelenk.",
    ];

    // Dritter Katalog nur für Orte aus ganz Deutschland, geschrieben nach dem Einbau der vollständigen Ortsliste
    public static readonly string[] Places =
    [
        "Sie wohnt seit 2019 in {{Frankfurt am Main}}.",
        "Patient kommt aus {{Halle (Saale)}} zur Zweitmeinung.",
        "Geboren in {{Gelsenkirchen}}, aufgewachsen in {{Castrop-Rauxel}}.",
        "Er arbeitet in {{Oberammergau}} als Schreiner.",
        "Die Familie ist aus {{Schwäbisch Hall}} hergezogen.",
        "Zuständig ist das Gesundheitsamt im Landkreis {{Grafschaft Bentheim}}.",
        "Wohnhaft im Kreis {{Steinfurt}}, arbeitet im {{Rhein-Sieg-Kreis}}.",
        "Samtgemeinde {{Emlichheim}}, Ortsteil {{Laar}}.",
        "Umzug von {{Görlitz}} nach {{Zittau}} geplant.",
        "Die Tochter lebt in {{Neustadt an der Weinstraße}}.",
        "Unfall auf dem Weg nach {{Quakenbrück}}.",
        "Adresse: Dorfstraße 3, 17498 {{Weitenhagen}}",
        "Sie pendelt täglich von {{Bad Oeynhausen}} nach {{Bielefeld}}.",
        "Kommt aus {{Wiesmoor}} in Ostfriesland.",
        "Reha in {{Bad Kissingen}} ab nächster Woche.",
        "Die Mutter wohnt in {{Treuenbrietzen}}.",
        "Er stammt aus {{Pfaffenhofen an der Ilm}}.",
        "Seit dem Umzug nach {{Hoyerswerda}} kein Hausarzt.",
        "Zuweisung aus {{Heilbronn}}, Nachsorge in {{Öhringen}}.",
        "Wohnort: {{Twist}}",
    ];

    public static readonly string[] PlacesWithoutPeople =
    [
        "Nach Belastung Schmerzen im Knie, in Ruhe beschwerdefrei.",
        "Bei Fieber über 39 Grad Paracetamol geben.",
        "In Rückenlage Schwindel, in Seitenlage besser.",
        "Aus Angst vor der Spritze hat sie abgesagt.",
        "Nach Absprache mit dem Kollegen Dosis erhöhen.",
        "In Narkose wurde der Zahn gezogen.",
        "Von Hand geschrieben, bitte abtippen.",
        "Bei Bedarf eine Tablette, höchstens drei am Tag.",
        "In Kürze kommen die Laborwerte.",
        "Nach Einnahme von Ibuprofen Magenschmerzen.",
        "Seit Wochen Husten, in letzter Zeit auch Auswurf.",
        "Die Wunde ist in Heilung, keine Rötung.",
        "Bei Verdacht auf Thrombose sofort vorstellen.",
        "Nach Rücksprache mit der Kasse wird die Reha bezahlt.",
        "Im Kreis der Familie wurde der Geburtstag gefeiert.",
        "Die Stadt hat die Buslinie geändert.",
        "Aus Versehen doppelte Dosis genommen.",
        "In Teilzeit arbeiten, ab Montag wieder voll.",
        "Nach Sturz Prellung am Oberschenkel.",
        "Bei Übelkeit Vomex, bei Schmerzen Novalgin.",
    ];

    [Fact]
    public async Task Erkennung_von_Orten()
    {
        var (recall, falsePositives) = await Measure(Places, PlacesWithoutPeople);
        Assert.True(recall >= 98, $"Erkannt nur {recall:0.0} %");
        Assert.Equal(0, falsePositives);
    }

    [Fact]
    public async Task Erkennung_mit_neuen_Saetzen()
    {
        var (recall, falsePositives) = await Measure(Unseen, UnseenWithoutPeople);
        Assert.True(recall >= 98, $"Erkannt nur {recall:0.0} %");
        Assert.Equal(0, falsePositives);
    }

    [Fact]
    public async Task Erkennung_im_Pruefkatalog()
    {
        var (recall, falsePositives) = await Measure(WithPeople, WithoutPeople);
        Assert.True(recall >= 98, $"Erkannt nur {recall:0.0} %");
        Assert.Equal(0, falsePositives);
    }

    private async Task<(double Recall, int FalsePositives)> Measure(string[] withPeople, string[] withoutPeople)
    {
        var pseudonymizer = Pseudonymizer.CreateDefault();
        int total = 0, found = 0, falsePositives = 0;

        foreach (var marked in withPeople)
        {
            var (text, expected) = Parse(marked);
            var result = await pseudonymizer.PseudonymizeAsync(text, new PseudonymMap());
            foreach (var e in expected)
            {
                total++;
                if (!Regex.IsMatch(result, $@"(?<![\p{{L}}]){Regex.Escape(e.Value)}(?![\p{{L}}])"))
                    found++;
                else
                    output.WriteLine($"VERPASST  {e.Value,-18} in: {result.Replace('\n', ' ')}");
            }
        }

        foreach (var text in withoutPeople)
        {
            var result = await pseudonymizer.PseudonymizeAsync(text, new PseudonymMap());
            var wrong = NameOrPlacePlaceholder().Matches(result).Count;
            if (wrong > 0)
            {
                falsePositives += wrong;
                output.WriteLine($"FALSCH    {result}");
            }
        }

        var recall = 100.0 * found / total;
        output.WriteLine($"Erkannt: {found} von {total} ({recall:0.0} %), falsch ersetzt: {falsePositives}");
        return (recall, falsePositives);
    }

    private record Expected(string Value, EntityType Type);

    private static (string Text, List<Expected> Expected) Parse(string marked)
    {
        var expected = new List<Expected>();
        var text = Marker().Replace(marked, m =>
        {
            var isName = m.Groups["name"].Success;
            var value = isName ? m.Groups["name"].Value : m.Groups["place"].Value;
            expected.Add(new Expected(value, isName ? EntityType.Name : EntityType.City));
            return value;
        });
        return (text, expected);
    }

    [GeneratedRegex(@"\[\[(?<name>[^\]]+)\]\]|\{\{(?<place>[^}]+)\}\}")]
    private static partial Regex Marker();

    [GeneratedRegex(@"\[(?:NAME|ORT)_\d+\]")]
    private static partial Regex NameOrPlacePlaceholder();
}
