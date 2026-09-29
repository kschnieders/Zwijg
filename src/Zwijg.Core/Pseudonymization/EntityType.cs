namespace Zwijg.Core.Pseudonymization;

// Reihenfolge ist wichtig: bei gleicher Fundstelle gewinnt der kleinere Wert.
public enum EntityType
{
    // Eigene Schutzregel der Praxis, das Label kommt aus der Regel
    Custom,
    Name,
    BirthDate,
    InsuranceNumber,
    Iban,
    Email,
    Phone,
    Address,

    // Sonstige Kennnummern wie Patienten, Fall oder Mitgliedsnummer
    Identifier,
    City,
    Date
}

public enum Sensitivity
{
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3
}

public static class EntityTypeInfo
{
    public static string Label(EntityType type) => type switch
    {
        EntityType.Custom => "EIGENE",
        EntityType.Name => "NAME",
        EntityType.BirthDate => "GEBURTSDATUM",
        EntityType.InsuranceNumber => "VERSICHERTENNR",
        EntityType.Iban => "IBAN",
        EntityType.Email => "EMAIL",
        EntityType.Phone => "TELEFON",
        EntityType.Address => "ADRESSE",
        EntityType.Identifier => "NUMMER",
        EntityType.City => "ORT",
        EntityType.Date => "DATUM",
        _ => "DATEN"
    };

    public static Sensitivity SensitivityOf(EntityType type) => type switch
    {
        EntityType.BirthDate or EntityType.InsuranceNumber or EntityType.Iban => Sensitivity.High,
        EntityType.Custom or EntityType.Name or EntityType.Email or EntityType.Phone or EntityType.Address
            or EntityType.Identifier => Sensitivity.Medium,
        _ => Sensitivity.Low
    };
}

// Label ist nur bei eigenen Regeln gesetzt, z.B. "PATIENTENNR"
public sealed record PiiMatch(EntityType Type, int Start, int Length, string Value, string? Label = null)
{
    public int End => Start + Length;
}
