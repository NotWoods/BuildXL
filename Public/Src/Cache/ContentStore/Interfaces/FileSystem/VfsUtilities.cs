// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace BuildXL.Cache.ContentStore.Interfaces.FileSystem
{
    /// <summary>
    /// Defines utility methods for working with VFS
    /// </summary>
    public static class VfsUtilities
    {
        private static readonly string DirectorySeparatorCharString = Path.DirectorySeparatorChar.ToString();
        private static readonly char[] PathSplitChars = new[] { Path.DirectorySeparatorChar };
        private static readonly char[] FilePlacementInfoFileNameSplitChars = new[] { '_' };

        /// <summary>
        /// Gets whether a path is contained in another path
        /// </summary>
        public static bool IsPathWithin(this string path, string candidateParent)
        {
            if (path.Length <= candidateParent.Length || !path.StartsWith(candidateParent, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (candidateParent.EndsWith(DirectorySeparatorCharString))
            {
                return true;
            }

            return path[candidateParent.Length] == Path.DirectorySeparatorChar;
        }

        /// <summary>
        /// Gets whether a path is contained in another path and returns the relative path from <paramref name="candidateParent"/> if <paramref name="path"/> is a subpath.
        /// </summary>
        public static bool TryGetRelativePath(this string path, string candidateParent, [NotNullWhen(true)]out string? relativePath)
        {
            if (path.IsPathWithin(candidateParent))
            {
                relativePath = path.Substring(candidateParent.Length + (candidateParent.EndsWith(DirectorySeparatorCharString) ? 0 : 1));
                return true;
            }
            else
            {
                relativePath = default;
                return false;
            }
        }
    }
}
