using System.Text;

namespace WordRoom.Data
{
    /// <summary>Same surface normalization as Python kw_data.normalize_word.</summary>
    public static class WordNormalizer
    {
        /// <summary>NFC, trailing homograph digits removed, '-' removed, '^' → space, whitespace runs collapsed.</summary>
        public static string Normalize(string word)
        {
            if (word == null) return string.Empty;
            var s = word.Normalize(NormalizationForm.FormC);
            var end = s.Length;
            while (end > 0 && char.IsDigit(s[end - 1])) end--;
            var sb = new StringBuilder(end);
            var pendingSpace = false;
            for (var i = 0; i < end; i++)
            {
                var c = s[i];
                if (c == '-') continue;
                if (c == '^') c = ' ';
                if (char.IsWhiteSpace(c))
                {
                    pendingSpace = sb.Length > 0;
                    continue;
                }
                if (pendingSpace)
                {
                    sb.Append(' ');
                    pendingSpace = false;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>Python len(): number of Unicode code points.</summary>
        public static int CodePointLength(string s)
        {
            var n = 0;
            for (var i = 0; i < s.Length; i++)
            {
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
                n++;
            }
            return n;
        }
    }
}
