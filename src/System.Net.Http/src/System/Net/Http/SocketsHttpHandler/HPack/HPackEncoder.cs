// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;

namespace System.Net.Http.HPack
{
    internal static class HPackEncoder
    {
        // Things we should add:
        // * Huffman encoding
        //      
        // Things we should consider adding:
        // * Dynamic table encoding:
        //   This would make the encoder stateful, which complicates things significantly.
        //   Additionally, it's not clear exactly what strings we would add to the dynamic table
        //   without some additional guidance from the user about this.
        //   So for now, don't do dynamic encoding.

        /// <summary>Encodes an "Indexed Header Field".</summary>
        public static bool EncodeIndexedHeaderField(int index, Span<byte> destination, out int bytesWritten)
        {
            // From https://tools.ietf.org/html/rfc7541#section-6.1
            // ----------------------------------------------------
            //   0   1   2   3   4   5   6   7
            // +---+---+---+---+---+---+---+---+
            // | 1 |        Index (7+)         |
            // +---+---------------------------+

            if (destination.Length != 0)
            {
                destination[0] = 0x80;
                return IntegerEncoder.Encode(index, 7, destination, out bytesWritten);
            }

            bytesWritten = 0;
            return false;
        }

        /// <summary>Encodes a "Literal Header Field without Indexing".</summary>
        public static bool EncodeLiteralHeaderFieldWithoutIndexing(int index, string value, Span<byte> destination, out int bytesWritten)
        {
            // From https://tools.ietf.org/html/rfc7541#section-6.2.2
            // ------------------------------------------------------
            //   0   1   2   3   4   5   6   7
            // +---+---+---+---+---+---+---+---+
            // | 0 | 0 | 0 | 0 |  Index (4+)   |
            // +---+---+-----------------------+
            // | H |     Value Length (7+)     |
            // +---+---------------------------+
            // | Value String (Length octets)  |
            // +-------------------------------+

            if ((uint)destination.Length >= 2)
            {
                destination[0] = 0;
                if (IntegerEncoder.Encode(index, 4, destination, out int indexLength))
                {
                    Debug.Assert(indexLength >= 1);
                    if (EncodeStringLiteral(value, destination.Slice(indexLength), out int nameLength, toLower: false))
                    {
                        bytesWritten = indexLength + nameLength;
                        return true;
                    }
                }
            }

            bytesWritten = 0;
            return false;
        }

        /// <summary>
        /// Encodes a "Literal Header Field without Indexing", but only the index portion;
        /// a subsequent call to <see cref="EncodeStringLiteral"/> must be used to encode the associated value.
        /// </summary>
        public static bool EncodeLiteralHeaderFieldWithoutIndexing(int index, Span<byte> destination, out int bytesWritten)
        {
            // From https://tools.ietf.org/html/rfc7541#section-6.2.2
            // ------------------------------------------------------
            //   0   1   2   3   4   5   6   7
            // +---+---+---+---+---+---+---+---+
            // | 0 | 0 | 0 | 0 |  Index (4+)   |
            // +---+---+-----------------------+
            //
            // ... expected after this:
            //
            // | H |     Value Length (7+)     |
            // +---+---------------------------+
            // | Value String (Length octets)  |
            // +-------------------------------+

            if ((uint)destination.Length != 0)
            {
                destination[0] = 0;
                if (IntegerEncoder.Encode(index, 4, destination, out int indexLength))
                {
                    Debug.Assert(indexLength >= 1);
                    bytesWritten = indexLength;
                    return true;
                }
            }

            bytesWritten = 0;
            return false;
        }

        /// <summary>Encodes a "Literal Header Field without Indexing - New Name".</summary>
        public static bool EncodeLiteralHeaderFieldWithoutIndexingNewName(string name, string value, Span<byte> destination, out int bytesWritten)
        {
            // From https://tools.ietf.org/html/rfc7541#section-6.2.2
            // ------------------------------------------------------
            //   0   1   2   3   4   5   6   7
            // +---+---+---+---+---+---+---+---+
            // | 0 | 0 | 0 | 0 |       0       |
            // +---+---+-----------------------+
            // | H |     Name Length (7+)      |
            // +---+---------------------------+
            // |  Name String (Length octets)  |
            // +---+---------------------------+
            // | H |     Value Length (7+)     |
            // +---+---------------------------+
            // | Value String (Length octets)  |
            // +-------------------------------+

            if ((uint)destination.Length >= 3)
            {
                destination[0] = 0;
                if (EncodeStringLiteral(name, destination.Slice(1), out int nameLength, toLower: true) &&
                    EncodeStringLiteral(value, destination.Slice(1 + nameLength), out int valueLength, toLower: false))
                {
                    bytesWritten = 1 + nameLength + valueLength;
                    return true;
                }
            }

            bytesWritten = 0;
            return false;
        }

        /// <summary>
        /// Encodes a "Literal Header Field without Indexing - New Name", but only the name portion;
        /// a subsequent call to <see cref="EncodeStringLiteral"/> must be used to encode the associated value.
        /// </summary>
        public static bool EncodeLiteralHeaderFieldWithoutIndexingNewName(string name, Span<byte> destination, out int bytesWritten)
        {
            // From https://tools.ietf.org/html/rfc7541#section-6.2.2
            // ------------------------------------------------------
            //   0   1   2   3   4   5   6   7
            // +---+---+---+---+---+---+---+---+
            // | 0 | 0 | 0 | 0 |       0       |
            // +---+---+-----------------------+
            // | H |     Name Length (7+)      |
            // +---+---------------------------+
            // |  Name String (Length octets)  |
            // +---+---------------------------+
            //
            // ... expected after this:
            //
            // | H |     Value Length (7+)     |
            // +---+---------------------------+
            // | Value String (Length octets)  |
            // +-------------------------------+

            if ((uint)destination.Length >= 2)
            {
                destination[0] = 0;
                if (EncodeStringLiteral(name, destination.Slice(1), out int nameLength, toLower: true))
                {
                    bytesWritten = 1 + nameLength;
                    return true;
                }
            }

            bytesWritten = 0;
            return false;
        }

        public static bool EncodeStringLiteral(string value, Span<byte> destination, out int bytesWritten, bool toLower)
        {
            // From https://tools.ietf.org/html/rfc7541#section-5.2
            // ------------------------------------------------------
            //   0   1   2   3   4   5   6   7
            // +---+---+---+---+---+---+---+---+
            // | H |    String Length (7+)     |
            // +---+---------------------------+
            // |  String Data (Length octets)  |
            // +-------------------------------+

            if (destination.Length != 0)
            {
                destination[0] = 0; // TODO: Use Huffman encoding
                if (IntegerEncoder.Encode(value.Length, 7, destination, out int integerLength))
                {
                    Debug.Assert(integerLength >= 1);
                    
                    destination = destination.Slice(integerLength);
                    if (value.Length <= destination.Length)
                    {
                        if (toLower)
                        {
                            for (int i = 0; i < value.Length; i++)
                            {
                                char c = value[i];
                                destination[i] = (byte)((uint)(c - 'A') <= ('Z' - 'A') ? c | 0x20 : c);
                            }
                        }
                        else
                        {
                            for (int i = 0; i < value.Length; i++)
                            {
                                destination[i] = (byte)value[i];
                            }
                        }

                        bytesWritten = integerLength + value.Length;
                        return true;
                    }
                }
            }

            bytesWritten = 0;
            return false;
        }

        /// <summary>
        /// Encodes a "Literal Header Field without Indexing" to a new array, but only the index portion;
        /// a subsequent call to <see cref="EncodeStringLiteral"/> must be used to encode the associated value.
        /// </summary>
        public static byte[] EncodeLiteralHeaderFieldWithoutIndexingToAllocatedArray(int index)
        {
            Span<byte> span = stackalloc byte[256];
            bool success = EncodeLiteralHeaderFieldWithoutIndexing(index, span, out int length);
            Debug.Assert(success, $"Stack-allocated space was too small for index '{index}'.");
            return span.Slice(0, length).ToArray();
        }

        /// <summary>
        /// Encodes a "Literal Header Field without Indexing - New Name" to a new array, but only the name portion;
        /// a subsequent call to <see cref="EncodeStringLiteral"/> must be used to encode the associated value.
        /// </summary>
        public static byte[] EncodeLiteralHeaderFieldWithoutIndexingNewNameToAllocatedArray(string name)
        {
            Span<byte> span = stackalloc byte[256];
            bool success = EncodeLiteralHeaderFieldWithoutIndexingNewName(name, span, out int length);
            Debug.Assert(success, $"Stack-allocated space was too small for \"{name}\".");
            return span.Slice(0, length).ToArray();
        }

        /// <summary>Encodes a "Literal Header Field without Indexing" to a new array.</summary>
        public static byte[] EncodeLiteralHeaderFieldWithoutIndexingToAllocatedArray(int index, string value)
        {
            Span<byte> span =
#if DEBUG
                stackalloc byte[4]; // to validate growth algorithm
#else
                stackalloc byte[512];
#endif
            while (true)
            {
                if (EncodeLiteralHeaderFieldWithoutIndexing(index, value, span, out int length))
                {
                    return span.Slice(0, length).ToArray();
                }

                // This is a rare path, only used once per HTTP/2 connection and only
                // for very long host names.  Just allocate rather than complicate
                // the code with ArrayPool usage.  In practice we should never hit this,
                // as hostnames should be <= 255 characters.
                span = new byte[span.Length * 2];
            }
        }
    }
}
