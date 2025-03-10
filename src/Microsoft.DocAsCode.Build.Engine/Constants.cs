﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.DocAsCode.Build.Engine
{
    internal static class Constants
    {
        /// <summary>
        /// TODO: how to handle multi-language
        /// </summary>
        public const string DefaultLanguage = "csharp";

        public const string ManifestFileName = "manifest.json";

        public const string TemplateDirectoryName = "templates";


        public static class OPSEnvironmentVariable
        {
            public const string SystemMetadata = "_op_systemMetadata";
            public const string OpPublishTargetSiteHostName = "_op_publishTargetSiteHostName";
        }
    }
}
