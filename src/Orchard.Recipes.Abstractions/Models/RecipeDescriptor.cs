﻿using Microsoft.Extensions.FileProviders;
using System;

namespace Orchard.Recipes.Models
{
    public class RecipeDescriptor
    {
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public string Author { get; set; }
        public string WebSite { get; set; }
        public string Version { get; set; }
        public bool IsSetupRecipe { get; set; }
        public DateTime? ExportUtc { get; set; }
        public string[] Categories { get; set; }
        public string[] Tags { get; set; }

        public string RecipeFileName { get; set; }
        public IFileProvider RecipeFileProvider { get; set; }

        public IFileInfo GetRecipeFile()
        {
            return RecipeFileProvider.GetFileInfo(RecipeFileName);
        }
    }
}