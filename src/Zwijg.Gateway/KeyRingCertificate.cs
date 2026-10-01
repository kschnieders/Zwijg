using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.Options;

namespace Zwijg.Gateway;

// Zertifikat aus Zwijg:KeyProtection, einmal geladen. Null, wenn keins eingestellt ist.
// Ein falscher Pfad oder ein falsches Passwort wirft, damit Zwijg dann gar nicht erst startet.
public sealed class KeyRingCertificate(IOptions<GatewayOptions> options)
{
    private readonly Lazy<X509Certificate2?> _cert = new(() => Load(options.Value.KeyProtection));

    public X509Certificate2? Value => _cert.Value;

    private static X509Certificate2? Load(KeyProtectionOptions o)
    {
        if (string.IsNullOrWhiteSpace(o.CertificatePath))
            return null;

        // Privater Schlüssel nur im Speicher, nicht im Schlüsselspeicher des Systems. macOS kann das nicht.
        var flags = OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;
        var cert = X509CertificateLoader.LoadPkcs12FromFile(o.CertificatePath, o.CertificatePassword, flags);
        if (cert.GetRSAPrivateKey() == null)
            throw new InvalidOperationException($"Das Zertifikat in {o.CertificatePath} braucht einen privaten RSA Schlüssel.");

        return cert;
    }

    // Ein Zertifikat wirkt nur auf neue Schlüssel. Ist der aktive Schlüssel noch ohne Zertifikat gespeichert,
    // gleich einen neuen anlegen, statt bis zum Ablauf des alten (bis zu 90 Tage) weiter mit ihm zu verschlüsseln.
    // Alte Schlüssel bleiben, damit ältere Daten lesbar bleiben.
    public static void EnsureProtectedDefaultKey(IServiceProvider services)
    {
        if (services.GetRequiredService<KeyRingCertificate>().Value == null)
            return;

        var options = services.GetRequiredService<IOptions<KeyManagementOptions>>().Value;
        var protectedIds = options.XmlRepository!.GetAllElements()
            .Where(e => e.Name == "key" && e.Descendants().Where(d => d.Name.LocalName == "encryptedSecret")
                .Any(s => ((string?)s.Attribute("decryptorType"))?.Contains(nameof(KeyRingCertificateDecryptor), StringComparison.Ordinal) == true))
            .Select(e => (string?)e.Attribute("id"))
            .Where(id => id != null && Guid.TryParse(id, out _))
            .Select(id => Guid.Parse(id!))
            .ToHashSet();

        var keyManager = services.GetRequiredService<IKeyManager>();
        var now = DateTimeOffset.UtcNow;
        var hasProtectedActiveKey = keyManager.GetAllKeys()
            .Any(k => !k.IsRevoked && k.ActivationDate <= now && k.ExpirationDate > now && protectedIds.Contains(k.KeyId));
        if (!hasProtectedActiveKey)
            keyManager.CreateNewKey(now, now + options.NewKeyLifetime);
    }
}

// Verschlüsselt wie ProtectKeysWithCertificate, verweist zum Entschlüsseln aber auf KeyRingCertificateDecryptor.
// Der Decryptor von ASP.NET findet das Zertifikat nur im Zertifikatsspeicher des Systems oder über eine
// interne Einstellung, die das fertige Zertifikat schon beim Registrieren der Dienste braucht.
public sealed class KeyRingCertificateEncryptor(X509Certificate2 cert, ILoggerFactory logs) : IXmlEncryptor
{
    private readonly CertificateXmlEncryptor _inner = new(cert, logs);

    public EncryptedXmlInfo Encrypt(XElement plaintextElement) =>
        new(_inner.Encrypt(plaintextElement).EncryptedElement, typeof(KeyRingCertificateDecryptor));
}

// Der Typname steht in den Schlüsseldateien. Klasse deshalb nicht umbenennen oder verschieben.
public sealed class KeyRingCertificateDecryptor(IServiceProvider services) : IXmlDecryptor
{
    public XElement Decrypt(XElement encryptedElement)
    {
        var cert = services.GetRequiredService<KeyRingCertificate>().Value
            ?? throw new InvalidOperationException("Die Schlüssel sind mit einem Zertifikat verschlüsselt, Zwijg:KeyProtection:CertificatePath fehlt.");

        // Wie bei ASP.NET: in ein Hilfselement packen, damit das Ergebnis wieder ein einzelnes Element ist
        var doc = new XmlDocument { PreserveWhitespace = true };
        using (var reader = new XElement("root", encryptedElement).CreateReader())
            doc.Load(reader);

        new CertificateEncryptedXml(doc, cert).DecryptDocument();
        return XElement.Parse(doc.DocumentElement!.FirstChild!.OuterXml);
    }

    // Nimmt für den Sitzungsschlüssel das eigene Zertifikat statt im Zertifikatsspeicher zu suchen
    private sealed class CertificateEncryptedXml(XmlDocument doc, X509Certificate2 cert) : EncryptedXml(doc)
    {
        public override byte[]? DecryptEncryptedKey(EncryptedKey encryptedKey)
        {
            foreach (var clause in encryptedKey.KeyInfo)
            {
                if (clause is not KeyInfoX509Data data)
                    continue;

                foreach (var candidate in data.Certificates?.OfType<X509Certificate2>() ?? [])
                {
                    if (!candidate.Thumbprint.Equals(cert.Thumbprint, StringComparison.OrdinalIgnoreCase))
                        continue;

                    using var rsa = cert.GetRSAPrivateKey()!;
                    var oaep = encryptedKey.EncryptionMethod?.KeyAlgorithm == XmlEncRSAOAEPUrl;
                    return DecryptKey(encryptedKey.CipherData.CipherValue!, rsa, oaep);
                }
            }

            throw new CryptographicException("Die Schlüssel sind mit einem anderen Zertifikat verschlüsselt.");
        }
    }
}
