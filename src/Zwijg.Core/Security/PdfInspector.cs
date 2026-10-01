using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace Zwijg.Core.Security;

public sealed record PdfInspection(string VisibleText, string HiddenText, int PageCount)
{
    public bool HasHiddenText => HiddenText.Trim().Length > 0;
}

// Liest den Text aus einem PDF und trennt sichtbaren von verstecktem Text.
// Versteckt heißt: weiße Schrift, winzige Schrift, unsichtbarer Render-Modus (3 und 7)
// oder Text außerhalb der sichtbaren Seite (CropBox). Nur der sichtbare Text geht später weiter.
// Das ist eine Heuristik. Nicht erkannt werden z.B. Text unter einem Bild, Schrift in der Farbe
// eines farbigen Hintergrunds, sehr helles Grau oder stark gestauchte Schrift (Tz).
// Der Text wird danach trotzdem auf Manipulationsversuche geprüft.
public static class PdfInspector
{
    private const double MinPointSize = 2.0;
    private const double WhiteThreshold = 0.95;

    public static PdfInspection Inspect(Stream pdf)
    {
        using var doc = PdfDocument.Open(pdf);

        var visible = new StringBuilder();
        var hidden = new StringBuilder();
        var pages = 0;

        foreach (var page in doc.GetPages())
        {
            pages++;
            var visibleLetters = new List<Letter>();
            var hiddenLetters = new List<Letter>();

            foreach (var letter in page.Letters)
            {
                if (IsHidden(letter, page))
                    hiddenLetters.Add(letter);
                else
                    visibleLetters.Add(letter);
            }

            AppendLines(visible, visibleLetters);
            AppendLines(hidden, hiddenLetters);
        }

        return new PdfInspection(visible.ToString().Trim(), hidden.ToString().Trim(), pages);
    }

    private static bool IsHidden(Letter letter, Page page)
    {
        if (string.IsNullOrWhiteSpace(letter.Value))
            return false;

        if (letter.RenderingMode is TextRenderingMode.Neither or TextRenderingMode.NeitherClip)
            return true;

        if (letter.PointSize > 0 && letter.PointSize < MinPointSize)
            return true;

        var box = letter.BoundingBox;
        if (box.Right < 0 || box.Left > page.Width || box.Top < 0 || box.Bottom > page.Height)
            return true;

        var stroke = letter.RenderingMode is TextRenderingMode.Stroke or TextRenderingMode.StrokeClip;
        var color = stroke ? letter.StrokeColor : letter.FillColor;
        if (color != null)
        {
            var (r, g, b) = color.ToRGBValues();
            if (r >= WhiteThreshold && g >= WhiteThreshold && b >= WhiteThreshold)
                return true;
        }

        return false;
    }

    // Buchstaben zu Wörtern und Zeilen zusammensetzen, von oben nach unten.
    private static void AppendLines(StringBuilder sb, List<Letter> letters)
    {
        if (letters.Count == 0)
            return;

        // Leerzeichen im PDF kommen hier als eigene "Wörter" an, die brauchen wir nicht
        var words = NearestNeighbourWordExtractor.Instance.GetWords(letters)
            .Where(w => !string.IsNullOrWhiteSpace(w.Text))
            .OrderByDescending(w => w.BoundingBox.Bottom)
            .ThenBy(w => w.BoundingBox.Left)
            .ToList();

        var line = new List<Word>();
        double? lineBottom = null;

        foreach (var word in words)
        {
            var tolerance = Math.Max(word.BoundingBox.Height * 0.5, 1);
            if (lineBottom != null && Math.Abs(word.BoundingBox.Bottom - lineBottom.Value) > tolerance)
            {
                FlushLine(sb, line);
                line.Clear();
            }

            line.Add(word);
            lineBottom = word.BoundingBox.Bottom;
        }

        FlushLine(sb, line);
    }

    private static void FlushLine(StringBuilder sb, List<Word> line)
    {
        if (line.Count > 0)
            sb.AppendLine(string.Join(" ", line.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)));
    }
}
