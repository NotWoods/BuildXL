﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Utilities;
using BuildXL.Utilities.Serialization;

namespace BuildXL.Cache.ContentStore.Utils
{
    public static class SerializationExtensions
    {
        public static void Write(this BuildXLWriter writer, UnixTime value)
        {
            writer.WriteCompact(value.Value);
        }

        public static UnixTime ReadUnixTime(this BuildXLReader reader)
        {
            return new UnixTime(reader.ReadInt64Compact());
        }

        public static UnixTime ReadUnixTime(ref this SpanReader reader)
        {
            return new UnixTime(reader.ReadInt64Compact());
        }
    }
}
