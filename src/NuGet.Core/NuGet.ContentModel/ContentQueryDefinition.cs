// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace NuGet.ContentModel
{
    /// <summary>
    /// A set of patterns that can be used to query a set of file paths for items matching a provided criteria.
    /// </summary>
    public class PatternSet
    {
        public PatternSet(IDictionary<string, ContentPropertyDefinition> properties, IList<PatternDefinition> groupPatterns, IList<PatternDefinition> pathPatterns)
        {
            GroupPatterns = groupPatterns ?? new List<PatternDefinition>();
            PathPatterns = pathPatterns ?? new List<PatternDefinition>();
            PropertyDefinitions = properties ?? new Dictionary<string, ContentPropertyDefinition>();
        }

        /// <summary>
        /// Patterns used to select a group of items that matches the criteria
        /// </summary>
        public IList<PatternDefinition> GroupPatterns { get; }

        /// <summary>
        /// Patterns used to select individual items that match the criteria
        /// </summary>
        public IList<PatternDefinition> PathPatterns { get; }

        /// <summary>
        /// Property definitions used for matching patterns
        /// </summary>
        public IDictionary<string, ContentPropertyDefinition> PropertyDefinitions { get; set; }
    }

    /// <summary>
    /// A pattern that can be used to match file paths given a provided criteria.
    /// </summary>
    /// <remarks>
    /// The pattern is defined as a sequence of literal path strings that must match exactly and property
    /// references,
    /// wrapped in {} characters, which are tested for compatibility with the consumer-provided criteria.
    /// <seealso cref="ContentPropertyDefinition" />
    /// </remarks>
    public class PatternDefinition
    {
        public string Pattern { get; }
        public IDictionary<string, object> Defaults { get; }

        /// <summary>
        /// Replacement token table.
        /// </summary>
        public PatternTable Table { get; }

        public PatternDefinition(string pattern)
            : this(pattern, table: null, defaults: new Dictionary<string, object>())
        {
        }

        public PatternDefinition(string pattern, PatternTable table)
            : this(pattern, table, new Dictionary<string, object>())
        {
        }

        public PatternDefinition(
            string pattern,
            PatternTable table,
            IDictionary<string, object> defaults)
        {
            Pattern = pattern;

            Table = table;
            Defaults = defaults;
        }

        public static implicit operator PatternDefinition(string pattern)
        {
            return new PatternDefinition(pattern);
        }
    }
}
