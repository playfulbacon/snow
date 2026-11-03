using System.Text;

namespace Snowfield.Net
{
    /// <summary>
    /// How a join code is written down. The service hands back six characters in a row, and six in a row is a
    /// thing nobody can read out over a voice call without losing their place — so a code is always shown in two
    /// groups of three ("8KT QZ4"), and the field you type one into shows those groups as dashes ("--- ---")
    /// that fill in as you go, so the shape of the code is on screen before you have any of it.
    ///
    /// The grouping is display only. <see cref="Normalize"/> strips it back out, and the code handed to the
    /// session service is always the bare characters.
    /// </summary>
    public static class JoinCodeFormat
    {
        /// <summary>Characters per group.</summary>
        public const int GroupSize = 3;

        /// <summary>Characters in a whole code. Session codes are six; the mask is built to that.</summary>
        public const int Length = 6;

        /// <summary>Stands in for a character you have not typed yet.</summary>
        public const char Blank = '-';

        const char Gap = ' ';

        /// <summary>A character a code can be made of. Codes are plain ASCII letters and digits, upper case.</summary>
        public static bool IsCodeChar(char c) =>
            (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

        /// <summary>The bare code: letters and digits only, upper case, groups and stray punctuation dropped.</summary>
        public static string Normalize(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            var sb = new StringBuilder(raw.Length);
            foreach (char c in raw)
                if (IsCodeChar(c)) sb.Append(char.ToUpperInvariant(c));
            return sb.ToString();
        }

        /// <summary>A whole code, grouped for reading: "8KTQZ4" becomes "8KT QZ4".</summary>
        public static string Pretty(string raw) => Group(Normalize(raw));

        /// <summary>
        /// A code being typed, as the field shows it: what has been entered, the rest as dashes, grouped the same
        /// way. "" becomes "--- ---", "8k" becomes "8K- ---", a full code becomes the same thing
        /// <see cref="Pretty"/> would give.
        /// </summary>
        public static string Mask(string typed)
        {
            string code = Normalize(typed);
            if (code.Length > Length) code = code.Substring(0, Length);
            return Group(code.PadRight(Length, Blank));
        }

        /// <summary>Spaces a string into groups of <see cref="GroupSize"/>. Any length, grouped from the left.</summary>
        static string Group(string s)
        {
            if (s.Length <= GroupSize) return s;
            var sb = new StringBuilder(s.Length + s.Length / GroupSize);
            for (int i = 0; i < s.Length; i++)
            {
                if (i > 0 && i % GroupSize == 0) sb.Append(Gap);
                sb.Append(s[i]);
            }
            return sb.ToString();
        }
    }
}
