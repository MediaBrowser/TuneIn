using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace MediaBrowser.Plugins.TuneIn.Security
{
    internal static class PublicAddressPolicy
    {
        public static bool IsPublic(IPAddress address)
        {
            if (address.IsIPv4MappedToIPv6)
                return IsPublic(address.MapToIPv4());
            var b = address.GetAddressBytes();
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                return !(b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224
                    || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                    || (b[0] == 169 && b[1] == 254)
                    || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                    || (b[0] == 192 && (b[1] == 168 || b[1] == 0
                        || (b[1] == 88 && b[2] == 99)))
                    || (b[0] == 198 && (b[1] == 18 || b[1] == 19
                        || (b[1] == 51 && b[2] == 100)))
                    || (b[0] == 203 && b[1] == 0 && b[2] == 113));
            }
            // Permit global unicast only; exclude documentation, transition and special-use ranges.
            return address.AddressFamily == AddressFamily.InterNetworkV6
                && (b[0] & 0xe0) == 0x20
                && !(b[0] == 0x20 && b[1] == 0x01 && b[2] < 0x02)
                && !(b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8)
                && !(b[0] == 0x20 && b[1] == 0x02)
                && !(b[0] == 0x3f && b[1] == 0xff && (b[2] & 0xf0) == 0);
        }

        public static Uri ValidateUri(string value, bool catalogue)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || (uri.Scheme != "https" && (catalogue || uri.Scheme != "http"))
                || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
                || uri.HostNameType == UriHostNameType.Unknown
                || uri.AbsoluteUri.Length > 4096
                || (catalogue && (uri.Port != 443
                    || !uri.Host.Equals("opml.radiotime.com", StringComparison.OrdinalIgnoreCase)
                    || !new[] { "/Browse.ashx", "/Describe.ashx", "/Tune.ashx" }.Contains(uri.AbsolutePath))))
                throw new InvalidOperationException("Untrusted TuneIn destination rejected.");
            return uri;
        }
    }
}
