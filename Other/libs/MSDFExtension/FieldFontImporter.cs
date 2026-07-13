using Microsoft.Xna.Framework.Content.Pipeline;
using MSDFData;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

#nullable enable

namespace MSDFExtension
{
    [ContentImporter(".ini", DisplayName = "Field Font Importer", DefaultProcessor = "FieldFontProcessor")]
    public class FieldFontImporter : ContentImporter<FontDescription>
    {
        public override FontDescription Import(string filename, ContentImporterContext context)
        {
            return Parse(filename);
        }

        private static FontDescription Parse(string filename)
        {
            var iniData = ReadIniFile(filename);

            if (!iniData.TryGetValue("font", out var fontSection))
                throw new Exception("Missing [font] section in INI file.");

            if (!fontSection.TryGetValue("path", out var path))
                throw new Exception("Missing 'path' in [font] section.");

            var characterSection = iniData.ContainsKey("characters") ? iniData["characters"] : fontSection;

            if (!characterSection.TryGetValue("ranges", out var ranges))
                throw new Exception("Missing 'ranges' in [characters] or [font] section.");

            var characters = ParseRanges(ranges);

            return new FontDescription(path, characters);
        }

        // Reads a simple INI file into a dictionary of sections, each containing a dictionary of key-value pairs
        private static Dictionary<string, Dictionary<string, string>> ReadIniFile(string filename)
        {
            var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string>? currentSection = null;

            foreach (var rawLine in File.ReadAllLines(filename))
            {
                var line = rawLine.Trim();

                if (string.IsNullOrEmpty(line) || line.StartsWith(";") || line.StartsWith("#"))
                    continue; // Skip comments and empty lines

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    var sectionName = line[1..^1].Trim();
                    currentSection = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    result[sectionName] = currentSection;
                }
                else if (currentSection != null && line.Contains('='))
                {
                    var parts = line.Split('=', 2);
                    var key = parts[0].Trim();
                    var value = parts[1].Trim();
                    currentSection[key] = value;
                }
                else
                {
                    throw new Exception($"Invalid line in INI file: {line}");
                }
            }

            return result;
        }

        private static char[] ParseRanges(string ranges)
        {
            var tuples = ParseTuples(ranges);

            var characters = new HashSet<char>();
            foreach (var tuple in tuples)
            {
                var parts = tuple.Split(',');
                if (parts.Length != 2)
                    throw new Exception($"Unexpected number of tuple elements in tuple: {tuple}");

                if (parts[0].Length != 1 || parts[1].Length != 1)
                    throw new Exception($"A tuple can only contain two characters separated by a comma: {tuple}");

                var start = parts[0][0];
                var end = parts[1][0];

                for (int i = start; i <= end; i++)
                    characters.Add((char)i);
            }

            return characters.ToArray();
        }

        private static IEnumerable<string> ParseTuples(string ranges)
        {
            var tuples = new List<string>();
            var start = -1;

            for (var i = 0; i < ranges.Length; i++)
            {
                var c = ranges[i];

                if (start > -1)
                {
                    if (c == ')')
                    {
                        var length = i - start - 1;
                        if (length < 1)
                            throw new Exception($"Empty tuple at position {start}");

                        tuples.Add(ranges.Substring(start + 1, length));
                        start = -1;
                    }
                    else if (c == '(')
                    {
                        throw new Exception($"Unexpected character '(', tuple was already opened at position {start}");
                    }
                }
                else if (c == '(')
                {
                    start = i;
                }
            }

            return tuples;
        }
    }
}
