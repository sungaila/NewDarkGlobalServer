﻿using System;
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

        public static int IntToHostOrder(this byte[] array)
        {
            ArgumentNullException.ThrowIfNull(array);

            if (array.Length != 4)
                throw new ArgumentOutOfRangeException(nameof(array));

            return IPAddress.NetworkToHostOrder((int)(array[0] + (array[1] << 8) + (array[2] << 16) + (array[3] << 24)));
        }

        public static int DirectPlayIntToHostOrder(this byte[] array)
        {
            ArgumentNullException.ThrowIfNull(array);

            if (array.Length != 4)
                throw new ArgumentOutOfRangeException(nameof(array));

            return array[0] + (array[1] << 8) + (array[2] << 16) + (array[3] << 24);
        }

        public static long LongToHostOrder(this byte[] array)
        {
            ArgumentNullException.ThrowIfNull(array);

            if (array.Length != 8)
                throw new ArgumentOutOfRangeException(nameof(array));

            return IPAddress.NetworkToHostOrder((long)(array[0] + (array[1] << 8) + (array[2] << 16) + (array[3] << 24) + (array[1] << 32) + (array[1] << 40) + (array[2] << 48) + (array[3] << 56)));
        }

        public static long DirectPlayLongToHostOrder(this byte[] array)
        {
            ArgumentNullException.ThrowIfNull(array);

            if (array.Length != 8)
                throw new ArgumentOutOfRangeException(nameof(array));

            return array[0] + (array[1] << 8) + (array[2] << 16) + (array[3] << 24) + (array[1] << 32) + (array[1] << 40) + (array[2] << 48) + (array[3] << 56);
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

        public static int ToNetworkOrder(this int value) => IPAddress.HostToNetworkOrder(value);

        public static long ToNetworkOrder(this long value) => IPAddress.HostToNetworkOrder(value);

        public static byte[] ToNetworkOrder(this byte[] value) => [.. value.Reverse()];
    }
}