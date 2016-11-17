// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Collections.Generic;
using NuGet.Packaging;
using NuGet.ProjectModel;

namespace NuGet.PackageManagement.VisualStudio
{
    /// <summary>
    /// Data model class to store deferred projects data.
    /// </summary>
    public sealed class DeferredProjectRestoreData
    {
        public DeferredProjectRestoreData(Dictionary<PackageReference, List<string>> packageReferenceDict, List<PackageSpec> packageSpecs)
        {
            PackageReferenceDict = packageReferenceDict;
            PackageSpecs = packageSpecs;
        }

        public Dictionary<PackageReference, List<string>> PackageReferenceDict { get; }

        public List<PackageSpec> PackageSpecs { get; }
    }
}
