using System;
using System.Linq;
using System.Net;

namespace Sungaila.NewDark.Core
{
    public static class Conversion
    {
        public static short ShortToHostOrder(this byte[] array)
        {
            ArgumentNullException.ThrowIfNull(array);

            if (array.Length != 2)
                throw new ArgumentOutOfRangeException(nameof(array));

            return IPAddress.NetworkToHostOrder((short)(array[0] + (array[1] << 8)));
        }

        public static short DirectPlayShortToHostOrder(this byte[] array)
        {
            ArgumentNullException.ThrowIfNull(array);

            if (array.Length != 2)
                throw new ArgumentOutOfRangeException(nameof(array));

            return (short)(array[0] + (array[1] << 8));
        }

        public static int DirectPlayIntToHostOrder(this byte[] array)
        {
            ArgumentNullException.ThrowIfNull(array);

            if (array.Length != 4)
                throw new ArgumentOutOfRangeException(nameof(array));

            return array[0] + (array[1] << 8) + (array[2] << 16) + (array[3] << 24);
        }

        public static Guid DirectPlayGuidToHostOrder(this byte[] array)
        {
            ArgumentNullException.ThrowIfNull(array);

            if (array.Length != 16)
                throw new ArgumentOutOfRangeException(nameof(array));

            return new Guid(array);
        }

        public static short ToNetworkOrder(this short value) => IPAddress.HostToNetworkOrder(value);

        public static ushort ToNetworkOrder(this ushort value) => (ushort)IPAddress.HostToNetworkOrder((short)value);

        public static byte[] ToNetworkOrder(this byte[] value) => [.. value.Reverse()];
    }
}