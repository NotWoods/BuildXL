// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

import * as XUnit from "Sdk.Managed.Testing.XUnit";

namespace DistributedTest {
    export declare const qualifier : BuildXLSdk.DefaultQualifierWithNet472;
    
    @@public
    export const dll = BuildXLSdk.cacheTest({
        assemblyName: "BuildXL.Cache.MemoizationStore.Distributed.Test",
        sources: globR(d`.`, "*.cs"),
        runTestArgs: {
                // Need to untrack the test output directory, because redis server tries to write some pdbs.
                untrackTestDirectory: true,
                parallelBucketCount: 8,
            },
        assemblyBindingRedirects: BuildXLSdk.cacheBindingRedirects(),
        skipTestRun: BuildXLSdk.restrictTestRunToSomeQualifiers,
        references: [
            ...addIf(BuildXLSdk.isFullFramework,
                NetFx.System.Xml.dll,
                NetFx.System.Xml.Linq.dll
            ),
            ContentStore.Hashing.dll,
            ContentStore.UtilitiesCore.dll,
            ContentStore.DistributedTest.dll,
            ContentStore.Distributed.dll,
            ContentStore.InterfacesTest.dll,
            ContentStore.Interfaces.dll,
            ContentStore.Library.dll,
            ContentStore.Test.dll,
            Distributed.dll,
            InterfacesTest.dll,
            Interfaces.dll,
            Library.dll,
            Test.dll,

            ...addIfLazy(BuildXLSdk.Flags.isVstsArtifactsEnabled, () => [Vsts.dll]),

            importFrom("BuildXL.Cache.DistributedCache.Host").Service.dll,
            importFrom("BuildXL.Cache.DistributedCache.Host").Configuration.dll,

            importFrom("BuildXL.Utilities").dll,
            importFrom("BuildXL.Utilities").Collections.dll,

            ...BuildXLSdk.fluentAssertionsWorkaround,
            ...importFrom("BuildXL.Cache.ContentStore").redisPackages,
            ...BuildXLSdk.bclAsyncPackages,
        ],
    });
}
