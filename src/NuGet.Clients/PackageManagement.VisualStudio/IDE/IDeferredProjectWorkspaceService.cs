// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace NuGet.PackageManagement.VisualStudio
{
    [Guid("113bc32d-986a-4ef4-b5d8-f1e3da44c0f8")]
    public interface IDeferredProjectWorkspaceService
    {
        Task<bool> EntityExists(string filePath);

        Task<IEnumerable<string>> GetProjectReferencesAsync(string projectFilePath);

        Task<IReadOnlyDictionary<string, string>> GetPackageReferencesAsync(string projectFilePath, CancellationToken cancellationToken);
    }
}
