// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using BuildXL.Utilities.Configuration.Resolvers;

namespace BuildXL.Utilities.Configuration
{
    /// <summary>
    /// Settings for CMake resolver
    /// </summary>
    public interface ICMakeResolverSettings : IResolverSettings
    {
        /// <summary>
        /// Root of the project, where CMakeLists.txt is
        /// </summary>
        AbsolutePath ProjectRoot { get; }

        /// <summary>
        /// Build directory, where the Ninja files will be generated and the built project will end up in
        /// </summary>
        RelativePath BuildDirectory { get; }

        /// <summary>
        /// Module name. Should be unique, as it is used as an id
        /// </summary>
        string ModuleName { get; }

        /// <summary>
        /// When cmake is first run in an empty build tree, it creates a CMakeCache.txt file
        /// and populates it with customizable settings for the project.
        /// This option may be used to specify a setting that takes priority over the projectís default value.
        /// [https://cmake.org/cmake/help/v3.6/manual/cmake.1.html]
        /// These values will be passed to the CMake generator as -D[key]=[value] arguments
        /// A value of null for a certain key indicates that the entry should be unset (-UKey)
        /// </summary>
        IReadOnlyDictionary<string, string> CacheEntries { get; }

        /// <summary>
        /// Collection of directories to search for cmake.exe.
        /// </summary>
        /// <remarks>
        /// If this is not defined, locations in %PATH% are used
        /// Locations are traversed in order
        /// </remarks>
        IReadOnlyList<DirectoryArtifact> CMakeSearchLocations { get; }

        /// <summary>
        /// User-specified untracked artifacts
        /// </summary>
        IUntrackingSettings UntrackingSettings { get;  }
    }
}
