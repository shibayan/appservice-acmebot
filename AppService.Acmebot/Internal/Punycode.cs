using System.Globalization;

namespace AppService.Acmebot.Internal
{
    public static class Punycode
    {
        private static readonly IdnMapping _idnMapping = new IdnMapping();

        public static string Encode(string unicode)
        {
            return _idnMapping.GetAscii(unicode);
        }

        public static string Decode(string ascii)
        {
            return _idnMapping.GetUnicode(ascii);
        }
    }
}
