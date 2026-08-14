using System.Globalization;
using System.Text.RegularExpressions;

namespace SignalTracker.Services
{
    public static class NrRrcOtaDecoder
    {
        public static string? TryDecodeSummary(params string?[] values)
        {
            var text = string.Join(" ", values.Where(value => !string.IsNullOrWhiteSpace(value)));
            if (string.IsNullOrWhiteSpace(text) ||
                !Regex.IsMatch(text, @"\bNR[-_ ]?RRC\b|NR_RRC|Full NR-RRC OTA message|NR RRC configuration", RegexOptions.IgnoreCase))
            {
                return null;
            }

            var match = Regex.Match(text, @"payload\[(\d+)\]=([0-9a-fA-F]+)", RegexOptions.IgnoreCase);
            if (!match.Success)
                return null;

            var hex = match.Groups[2].Value;
            var byteCount = hex.Length / 2;
            if (byteCount == 0)
                return null;

            var bytes = new byte[byteCount];
            for (var index = 0; index < byteCount; index++)
            {
                if (!byte.TryParse(hex.Substring(index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[index]))
                    return null;
            }

            var pci = ReadUIntLittleEndian(bytes, 7, 2);
            var arfcn = ReadUIntLittleEndian(bytes, 17, 4);
            var hasValidArfcn = arfcn.HasValue && arfcn.Value >= 0;
            var frequencyMhz = hasValidArfcn ? NrArfcnToMhz(arfcn!.Value) : null;
            var band = hasValidArfcn ? InferNrBand(arfcn!.Value) : null;
            var parts = new List<string>();

            if (pci.HasValue) parts.Add(pci.Value < 0 ? "NR PCI: NA" : $"NR PCI: {pci.Value}");
            if (arfcn.HasValue) parts.Add(arfcn.Value < 0 ? "NR ARFCN: NA" : $"NR ARFCN: {arfcn.Value}");
            if (frequencyMhz.HasValue) parts.Add($"NR Frequency: {frequencyMhz.Value.ToString("0.000", CultureInfo.InvariantCulture)} MHz");
            if (!string.IsNullOrWhiteSpace(band) && !string.Equals(band, "Unknown", StringComparison.OrdinalIgnoreCase)) parts.Add($"NR Band: {band}");

            return parts.Count == 0 ? null : string.Join(" | ", parts);
        }

        private static int? ReadUIntLittleEndian(byte[] bytes, int offset, int size)
        {
            if (offset < 0 || size <= 0 || offset + size > bytes.Length)
                return null;

            var value = 0;
            for (var index = 0; index < size; index++)
            {
                value += bytes[offset + index] << (index * 8);
            }
            return value;
        }

        private static double? NrArfcnToMhz(int nrArfcn)
        {
            if (nrArfcn < 0)
                return null;
            if (nrArfcn <= 599999)
                return nrArfcn * 0.005;
            if (nrArfcn <= 2016666)
                return 3000 + ((nrArfcn - 600000) * 0.015);
            return 24250.08 + ((nrArfcn - 2016667) * 0.06);
        }

        private static string InferNrBand(int nrArfcn)
        {
            if (nrArfcn >= 151600 && nrArfcn <= 160600)
                return "n28";
            if (nrArfcn >= 499200 && nrArfcn <= 537999)
                return "n41";
            if (nrArfcn >= 620000 && nrArfcn <= 653333)
                return "n78";
            return "Unknown";
        }
    }
}
