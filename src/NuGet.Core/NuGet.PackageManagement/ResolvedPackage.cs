// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using NuGet.Versioning;

namespace NuGet.PackageManagement
{
    public class ResolvedPackage
    {
        public ResolvedPackage(NuGetVersion latestVersion, bool isPackageExists)
        {
            LatestVersion = latestVersion;
            IsPackageExists = isPackageExists;
        }

        public NuGetVersion LatestVersion { get; }

        public bool IsPackageExists { get; }
    }
}
