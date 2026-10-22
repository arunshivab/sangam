using System.Text;
using System.Xml;
using System.Xml.Schema;

namespace Sangam.Identity.Infrastructure.Saml;

/// <summary>
/// rc.15 (ASVS V13.3.1): every SAML message Sangam receives is checked against the OASIS SAML 2.0 protocol schema (with the
/// assertion, XML Signature and XML Encryption schemas it imports) before any field is read. The schemas are built into the
/// assembly; nothing is fetched. A message that does not follow the schema is refused as unreadable, the same as malformed
/// XML. The check reads its own copy of the text, so the document whose signature is checked is never changed by it.
/// </summary>
public static class SamlSchema
{
    /// <summary>The most schema errors reported in one refusal.</summary>
    public const int MaxErrors = 5;

    private const string Prefix = "Sangam.Identity.Infrastructure.Saml.Schemas.";

    private static readonly Lazy<XmlSchemaSet> Schemas = new(Build, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Throws <see cref="InvalidDataException"/> when <paramref name="xml"/> is not a valid SAML 2.0 protocol message.</summary>
    /// <param name="xml">The message, as received.</param>
    public static void Validate(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        List<string> errors = [];
        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = SamlProtocol.MaxMessageBytes,
            ValidationType = ValidationType.Schema,
            Schemas = Schemas.Value,
        };

        // Warnings too (an element the schemas do not know is a warning), on top of the defaults.
        settings.ValidationFlags |= XmlSchemaValidationFlags.ReportValidationWarnings;
        settings.ValidationEventHandler += (_, e) =>
        {
            if (errors.Count < MaxErrors)
            {
                errors.Add(e.Message);
            }
        };

        using StringReader text = new(xml);
        using XmlReader reader = XmlReader.Create(text, settings);
        while (reader.Read())
        {
            // Reading is the check.
        }

        if (errors.Count > 0)
        {
            throw new InvalidDataException("The SAML message does not follow the SAML 2.0 schema: " + string.Join(" ", errors));
        }
    }

    private static XmlSchemaSet Build()
    {
        XmlSchemaSet set = new() { XmlResolver = new EmbeddedResolver() };
        set.Add(Read("saml-schema-protocol-2.0.xsd"));
        set.Compile();
        return set;
    }

    private static XmlSchema Read(string name)
    {
        using Stream stream = Open(name);
        // The XML Signature and Encryption schemas carry a DTD that only declares their namespaces again: it is skipped.
        using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
        return XmlSchema.Read(reader, null) ?? throw new InvalidOperationException($"The built-in schema {name} cannot be read.");
    }

    private static Stream Open(string name)
        => typeof(SamlSchema).Assembly.GetManifestResourceStream(Prefix + name) ?? throw new InvalidOperationException($"The built-in schema {name} is missing.");

    /// <summary>Resolves the schemas' imports to the built-in copies (by file name), and nothing else.</summary>
    private sealed class EmbeddedResolver : XmlResolver
    {
        public override object? GetEntity(Uri absoluteUri, string? role, Type? ofObjectToReturn)
        {
            ArgumentNullException.ThrowIfNull(absoluteUri);
            string name = absoluteUri.Segments[^1];
            if (typeof(SamlSchema).Assembly.GetManifestResourceNames().Contains(Prefix + name, StringComparer.Ordinal))
            {
                // Hand back the text without its DTD, which the schema reader would otherwise refuse.
                using Stream stream = Open(name);
                using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
                XmlDocument document = new() { XmlResolver = null };
                document.Load(reader);
                return new MemoryStream(Encoding.UTF8.GetBytes(document.DocumentElement!.OuterXml));
            }

            throw new XmlException($"The SAML schemas may import only the built-in schemas, not {absoluteUri}.");
        }

        public override Uri ResolveUri(Uri? baseUri, string? relativeUri)
            => baseUri is null || !baseUri.IsAbsoluteUri ? new Uri(new Uri("embedded:///"), relativeUri) : new Uri(baseUri, relativeUri);
    }
}
