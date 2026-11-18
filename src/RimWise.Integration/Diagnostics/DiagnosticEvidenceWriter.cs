using System.Text;
using System.Text.Json;

namespace RimWise.Integration.Diagnostics;

/// <summary>Export one evidence record at a time, without per-file or archive truncation.</summary>
internal static class DiagnosticEvidenceWriter
{
    internal static void Write(string path, string logical, Stream output, DiagnosticRedactor redactor,
        CancellationToken token, Action<long, string> recovered, Func<DateTimeOffset, bool>? inRun)
    {
        if (logical.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var source = StoredEvidence.OpenExportSnapshot(path, token);
                redactor.Json(source, output);
                return;
            }
            catch (JsonException)
            {
                // Parsing happens before writing, so no partial JSON entry exists here.
                // Preserve readable damaged evidence as a valid, explicitly labelled JSON object.
                using var source = OpenText(path, redactor, token);
                using var writer = new Utf8JsonWriter(output);
                writer.WriteStartObject();
                writer.WriteBoolean("recoveredMalformedDocument", true);
                writer.WriteStartArray("sanitizedRecords");
                long line = 0;
                foreach (var record in Records(source, token))
                { writer.WriteStringValue(redactor.RecoverMalformedJson(record)); line++; }
                writer.WriteEndArray(); writer.WriteEndObject();
                recovered(line, "malformed-json-preserved-as-sanitized-records");
                return;
            }
        }

        using var reader = OpenText(path, redactor, token);
        using var text = new StreamWriter(output, new UTF8Encoding(false, true), 65536, leaveOpen: true);
        var jsonLines = logical.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);
        long number = 0;
        foreach (var record in Records(reader, token))
        {
            number++;
            if (!jsonLines) { text.Write(redactor.Text(record)); continue; }
            if (string.IsNullOrWhiteSpace(record)) { text.Write(record); continue; }
            try
            {
                using var entry = JsonDocument.Parse(record);
                if (inRun is not null && entry.RootElement.ValueKind == JsonValueKind.Object && entry.RootElement.TryGetProperty("timestampUtc", out var time)
                    && time.TryGetDateTimeOffset(out var timestamp) && !inRun(timestamp)) continue;
                text.WriteLine(redactor.Json(record));
            }
            catch (JsonException)
            {
                // A partially written event still carries useful error evidence. Keep it
                // parseable and scrub escaped credential names instead of silently dropping it.
                text.WriteLine(JsonSerializer.Serialize(new
                {
                    recoveredMalformedRecord = true, sourceLine = number,
                    text = redactor.RecoverMalformedJson(record)
                }));
                recovered(number, "malformed-json-line-preserved");
            }
        }
    }

    private static TextReader OpenText(string path, DiagnosticRedactor redactor, CancellationToken token)
    {
        var reader = new StreamReader(StoredEvidence.OpenExportSnapshot(path, token), new UTF8Encoding(false, true), true, 65536);
        // A known secret can itself contain line breaks. Replace those literal values
        // before splitting records, retaining enough lookahead at each read boundary.
        var multiline = redactor.SecretValues().Where(s => s.Contains('\n') || s.Contains('\r')).ToArray();
        return multiline.Length == 0 ? reader : new LiteralSecretReader(reader, multiline, token);
    }

    private static IEnumerable<string> Records(TextReader reader, CancellationToken token)
    {
        var buffer = new char[65536];
        var record = new StringBuilder();
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();
            var start = 0;
            for (var i = 0; i < read; i++)
                if (buffer[i] == '\n')
                {
                    record.Append(buffer, start, i - start + 1);
                    yield return record.ToString(); record.Clear(); start = i + 1;
                }
            record.Append(buffer, start, read - start);
        }
        if (record.Length > 0) yield return record.ToString();
    }

    private sealed class LiteralSecretReader(TextReader source, string[] secrets, CancellationToken token) : TextReader
    {
        private readonly char[] buffer = new char[65536];
        private readonly int lookahead = secrets.Max(s => s.Length) - 1;
        private string pending = "", ready = "";
        private int position;
        private bool ended;

        public override int Read(char[] target, int offset, int count)
        {
            if (count == 0) return 0;
            while (position == ready.Length)
            {
                token.ThrowIfCancellationRequested();
                if (ended && pending.Length == 0) return 0;
                var read = ended ? 0 : source.Read(buffer, 0, buffer.Length);
                ended |= read == 0;
                var value = pending + new string(buffer, 0, read);
                var commit = ended ? value.Length : Math.Max(0, value.Length - lookahead);
                var transformed = new StringBuilder();
                var consumed = 0;
                while (consumed < commit)
                {
                    var next = value.Length; string? matched = null;
                    foreach (var secret in secrets)
                    {
                        var index = value.IndexOf(secret, consumed, StringComparison.Ordinal);
                        if (index >= 0 && index < next) { next = index; matched = secret; }
                    }
                    if (next >= commit) { transformed.Append(value, consumed, commit - consumed); consumed = commit; break; }
                    transformed.Append(value, consumed, next - consumed).Append("[REDACTED]");
                    consumed = next + matched!.Length;
                }
                pending = value[consumed..]; ready = transformed.ToString(); position = 0;
            }
            var length = Math.Min(count, ready.Length - position);
            ready.CopyTo(position, target, offset, length); position += length;
            return length;
        }
        protected override void Dispose(bool disposing) { if (disposing) source.Dispose(); base.Dispose(disposing); }
    }

    internal sealed class CountingStream(Stream output) : Stream
    {
        internal long Written { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Written;
        public override long Position { get => Written; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { output.Write(buffer, offset, count); Written += count; }
        public override void Write(ReadOnlySpan<byte> buffer) { output.Write(buffer); Written += buffer.Length; }
        public override void Flush() => output.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
