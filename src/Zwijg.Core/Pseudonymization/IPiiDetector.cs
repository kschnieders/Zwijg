namespace Zwijg.Core.Pseudonymization;

public interface IPiiDetector
{
    Task<IReadOnlyList<PiiMatch>> DetectAsync(string text, CancellationToken ct = default);
}
