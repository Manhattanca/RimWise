#nullable disable
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;

namespace RimWise.TestHelper
{
    // Shared pure formatter, also compiled into Integration for offline checks.
    // This snapshot is diagnostic data only, never a replacement candidate or execution authority.
    internal static class ProbeOrderEvidence
    {
        internal static XmlDocument Create(string nonce, int pid, string[] expected, string[] actual)
        {
            Guid ignored;
            if (!Guid.TryParseExact(nonce, "N", out ignored) || pid <= 0 || expected == null || actual == null
                || expected.Length > 20000 || actual.Length > 20000
                || expected.Concat(actual).Any(id => String.IsNullOrWhiteSpace(id) || id.Length > 512 || id.Any(Char.IsControl))
                || expected.Concat(actual).Sum(id => (long)id.Length) > 256 * 1024)
                throw new InvalidDataException("Invalid loaded order evidence.");
            int first = 0;
            while (first < Math.Min(expected.Length, actual.Length)
                && String.Equals(expected[first], actual[first], StringComparison.OrdinalIgnoreCase)) first++;
            bool match = first == expected.Length && first == actual.Length;
            var doc = new XmlDocument { XmlResolver = null };
            var root = doc.CreateElement("loadedOrder"); doc.AppendChild(root);
            root.SetAttribute("schema", "1"); root.SetAttribute("nonce", nonce);
            root.SetAttribute("pid", pid.ToString(CultureInfo.InvariantCulture));
            root.SetAttribute("matches", match ? "1" : "0");
            root.SetAttribute("firstDifference", (match ? 0 : first + 1).ToString(CultureInfo.InvariantCulture));
            foreach (var pair in new[] { new { Name = "expected", Values = expected }, new { Name = "actual", Values = actual } })
            {
                var list = doc.CreateElement(pair.Name); root.AppendChild(list);
                foreach (var id in pair.Values)
                {
                    var item = doc.CreateElement("li"); item.InnerText = id; list.AppendChild(item);
                }
            }
            if (Encoding.UTF8.GetByteCount(doc.OuterXml) > 1024 * 1024)
                throw new InvalidDataException("Loaded order evidence exceeds diagnostic limit.");
            return doc;
        }
    }
}
