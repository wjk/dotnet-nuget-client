// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;

namespace NuGet.ProjectManagement.Projects
{
    /// <summary>
    /// An exception that indicates the project has not been loaded. For example, in the case of CPS-based
    /// ProjectReference projects, this can mean that project has not been nominated yet.
    /// </summary>
    public class ProjectNotLoadedException : InvalidOperationException
    {
        public ProjectNotLoadedException(string message) : base(message)
        {
        }
    }
}
