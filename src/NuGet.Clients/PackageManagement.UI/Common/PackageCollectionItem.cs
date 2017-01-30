using NuGet.Packaging.Core;
using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NuGet.PackageManagement.UI
{
    /// <summary>
    /// id/version/autoreferenced
    /// </summary>
    internal class PackageCollectionItem : PackageIdentity
    {
        public bool AutoReferenced { get; }

        public PackageCollectionItem(string id, NuGetVersion version)
            : this(id, version, autoReferenced: false)
        {
        }

        public PackageCollectionItem(string id, NuGetVersion version, bool autoReferenced)
            : base(id, version)
        {
            AutoReferenced = autoReferenced;
        }
    }
}
