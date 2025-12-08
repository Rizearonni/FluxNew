using Avalonia.Controls;
using System;

namespace FluxNew
{
    public partial class PresetEditorWindow : Window
    {
        public PresetEditorWindow()
        {
            InitializeComponent();

            // Wire buttons
            var save = this.FindControl<Button>("SaveBtn");
            var cancel = this.FindControl<Button>("CancelBtn");
            if (cancel != null) cancel.Click += (_, __) => this.Close();
            if (save != null) save.Click += (_, __) => { SaveAndClose(); };

            // populate fields from WoWApi presets
            try
            {
                var presets = WoWApi.GetExpansionPresets();
                if (presets != null)
                {
                    if (presets.TryGetValue("vanilla", out var v))
                    {
                        var idBox = this.FindControl<TextBox>("VanillaProjectIdBox");
                        var verBox = this.FindControl<TextBox>("VanillaVersionBox");
                        var buildBox = this.FindControl<TextBox>("VanillaBuildBox");
                        var tocBox = this.FindControl<TextBox>("VanillaTOCBox");
                        if (idBox != null) idBox.Text = v.WOW_PROJECT_ID.ToString();
                        if (verBox != null) verBox.Text = v.Version;
                        if (buildBox != null) buildBox.Text = v.Build;
                        if (tocBox != null) tocBox.Text = v.TOC;
                    }
                    if (presets.TryGetValue("mists", out var m))
                    {
                        var idBox = this.FindControl<TextBox>("MistsProjectIdBox");
                        var verBox = this.FindControl<TextBox>("MistsVersionBox");
                        var buildBox = this.FindControl<TextBox>("MistsBuildBox");
                        var tocBox = this.FindControl<TextBox>("MistsTOCBox");
                        if (idBox != null) idBox.Text = m.WOW_PROJECT_ID.ToString();
                        if (verBox != null) verBox.Text = m.Version;
                        if (buildBox != null) buildBox.Text = m.Build;
                        if (tocBox != null) tocBox.Text = m.TOC;
                    }
                    if (presets.TryGetValue("mainline", out var r))
                    {
                        var idBox = this.FindControl<TextBox>("MainProjectIdBox");
                        var verBox = this.FindControl<TextBox>("MainVersionBox");
                        var buildBox = this.FindControl<TextBox>("MainBuildBox");
                        var tocBox = this.FindControl<TextBox>("MainTOCBox");
                        if (idBox != null) idBox.Text = r.WOW_PROJECT_ID.ToString();
                        if (verBox != null) verBox.Text = r.Version;
                        if (buildBox != null) buildBox.Text = r.Build;
                        if (tocBox != null) tocBox.Text = r.TOC;
                    }
                }
            }
            catch { }
        }

        private void SaveAndClose()
        {
            try
            {
                // Vanilla
                var vIdBox = this.FindControl<TextBox>("VanillaProjectIdBox");
                var vVerBox = this.FindControl<TextBox>("VanillaVersionBox");
                var vBuildBox = this.FindControl<TextBox>("VanillaBuildBox");
                var vTocBox = this.FindControl<TextBox>("VanillaTOCBox");
                var vId = vIdBox?.Text ?? "2";
                var vVer = vVerBox?.Text ?? "";
                var vBuild = vBuildBox?.Text ?? "";
                var vToc = vTocBox?.Text ?? "";
                if (int.TryParse(vId, out var vi))
                {
                    WoWApi.UpdateExpansionPreset("vanilla", new WoWApi.ExpansionPreset { WOW_PROJECT_ID = vi, Name = "Vanilla", Version = vVer, Build = vBuild, TOC = vToc });
                }

                // Mists
                var mIdBox = this.FindControl<TextBox>("MistsProjectIdBox");
                var mVerBox = this.FindControl<TextBox>("MistsVersionBox");
                var mBuildBox = this.FindControl<TextBox>("MistsBuildBox");
                var mTocBox = this.FindControl<TextBox>("MistsTOCBox");
                var mId = mIdBox?.Text ?? "4";
                var mVer = mVerBox?.Text ?? "";
                var mBuild = mBuildBox?.Text ?? "";
                var mToc = mTocBox?.Text ?? "";
                if (int.TryParse(mId, out var mi))
                {
                    WoWApi.UpdateExpansionPreset("mists", new WoWApi.ExpansionPreset { WOW_PROJECT_ID = mi, Name = "Mists", Version = mVer, Build = mBuild, TOC = mToc });
                }

                // Mainline
                var rIdBox = this.FindControl<TextBox>("MainProjectIdBox");
                var rVerBox = this.FindControl<TextBox>("MainVersionBox");
                var rBuildBox = this.FindControl<TextBox>("MainBuildBox");
                var rTocBox = this.FindControl<TextBox>("MainTOCBox");
                var rId = rIdBox?.Text ?? "1";
                var rVer = rVerBox?.Text ?? "";
                var rBuild = rBuildBox?.Text ?? "";
                var rToc = rTocBox?.Text ?? "";
                if (int.TryParse(rId, out var ri))
                {
                    WoWApi.UpdateExpansionPreset("mainline", new WoWApi.ExpansionPreset { WOW_PROJECT_ID = ri, Name = "Mainline", Version = rVer, Build = rBuild, TOC = rToc });
                }

                // close dialog
                this.Close();
            }
            catch { this.Close(); }
        }
    }
}
