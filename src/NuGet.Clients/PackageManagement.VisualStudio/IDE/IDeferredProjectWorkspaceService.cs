// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace NuGet.PackageManagement.VisualStudio
{
    public interface IDeferredProjectWorkspaceService
    {
        Task<bool> EntityExists(string filePath);

        Task<IEnumerable<string>> GetProjectReferencesAsync(string projectFilePath);
    }
}
