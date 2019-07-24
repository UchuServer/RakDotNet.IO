using System;
using System.IO;
using RakDotNet.IO;

namespace RakDotNet.Example
{
    public static class Program
    {
        public static void Main(string[] args)
        {
            var stream = new MemoryStream(100);

            using (var writer = new BitWriter(stream))
            {
                var input = long.MaxValue;

                Console.WriteLine($"Input (long) = {input}");

                writer.Write(input);

                var inBit = true;

                Console.WriteLine($"Input (bit) = {inBit}");

                writer.WriteBit(inBit);

                var inFloat = float.MaxValue;

                Console.WriteLine($"Input (float) = {inFloat}");

                writer.Write(inFloat);
            }

            using (var reader = new BitReader(stream))
            {
                var output = reader.Read<long>();

                Console.WriteLine($"Out   (long) = {output}");

                var outBit = reader.ReadBit();

                Console.WriteLine($"Out   (bit) = {outBit}");

                var outFloat = reader.Read<float>();

                Console.WriteLine($"Out   (float) = {outFloat}");
            }
        }
    }
}
