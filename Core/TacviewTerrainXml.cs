using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;

namespace VTOLVRTacviewSPRecorder.Core;

public static class TacviewTerrainXml
{
    public static string Build(TacviewTerrainDefinition definition)
    {
        if (definition == null)
        {
            throw new ArgumentNullException(nameof(definition));
        }

        var builder = new StringBuilder(2048);
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = true,
            IndentChars = "\t",
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.Replace,
            OmitXmlDeclaration = false
        };

        using (XmlWriter writer = XmlWriter.Create(new StringWriterWithEncoding(builder, settings.Encoding), settings))
        {
            writer.WriteStartDocument(true);
            writer.WriteStartElement("Resources");
            writer.WriteStartElement("CustomHeightmapList");
            writer.WriteStartElement("CustomHeightmap");
            writer.WriteAttributeString("Layer", "Real World");
            // Tacview 1.9.5 parses this attribute case-sensitively. `ID`
            // silently creates an unnamed Heightmap# entry and Auto Terrain
            // Switch cannot match the ACMI MapId; the required spelling is Id.
            writer.WriteAttributeString("Id", definition.TerrainId);

            writer.WriteElementString("File", definition.RawFileName);
            writer.WriteElementString("Visible", "false");
            writer.WriteElementString("BigEndian", "0");
            writer.WriteElementString("Signed", "true");
            writer.WriteElementString("Width", definition.Width.ToString(CultureInfo.InvariantCulture));
            writer.WriteElementString("Height", definition.Height.ToString(CultureInfo.InvariantCulture));
            writer.WriteElementString("AltitudeFactor", "1");
            writer.WriteElementString("AltitudeOffset", "0");
            writer.WriteElementString("Projection", "Quad");
            WritePoint(writer, "BottomLeft", definition.BottomLeft);
            WritePoint(writer, "BottomRight", definition.BottomRight);
            WritePoint(writer, "TopRight", definition.TopRight);
            WritePoint(writer, "TopLeft", definition.TopLeft);

            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return builder.ToString();
    }

    private static void WritePoint(XmlWriter writer, string name, TacviewTerrainGeoPoint point)
    {
        writer.WriteStartElement(name);
        writer.WriteElementString("Longitude", point.Longitude.ToString("R", CultureInfo.InvariantCulture));
        writer.WriteElementString("Latitude", point.Latitude.ToString("R", CultureInfo.InvariantCulture));
        writer.WriteEndElement();
    }

    private sealed class StringWriterWithEncoding : StringWriter
    {
        private readonly Encoding _encoding;

        public StringWriterWithEncoding(StringBuilder builder, Encoding encoding)
            : base(builder, CultureInfo.InvariantCulture)
        {
            _encoding = encoding;
        }

        public override Encoding Encoding => _encoding;
    }
}
