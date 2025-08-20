using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using MissionPlanner.Plugin;
using MissionPlanner.Utilities;

namespace ImageOverlayPlugin
{
    public class Plugin : MissionPlanner.Plugin.Plugin
    {
        public override string Name => "Image Overlay Plugin";
        public override string Version => "0.6";
        public override string Author => "IDPA";

        private ToolStripMenuItem _iopMenu;
        private ToolStripMenuItem _miConfig, _miReload, _miShowHide, _miLayers;

        // layers in memory (GUI only for Step 1)
        private readonly Dictionary<string, LayerInfo> _layers =
            new Dictionary<string, LayerInfo>(StringComparer.OrdinalIgnoreCase);
        private string _activeLayer; // name

        public override bool Init() => true;

        public override bool Loaded()
        {
            // 1) prove load
            MessageBox.Show("IOP plugin loaded (GUI only).", "IOP");

            // 2) add top menu
            var menu = Host.MainForm?
                .Controls
                .OfType<MenuStrip>()
                .FirstOrDefault() ?? Host.MainForm?.MainMenuStrip;

            if (menu == null)
            {
                MessageBox.Show("Could not locate main menu strip.", "IOP");
                return true; // don’t hard-fail MP
            }

            _iopMenu = new ToolStripMenuItem("IOP");
            _miConfig = new ToolStripMenuItem("Config…", null, (s, e) => DoConfig());
            _miReload = new ToolStripMenuItem("Reload", null, (s, e) => DoReloadStub());
            _miShowHide = new ToolStripMenuItem("Show / Hide", null, (s, e) => DoShowHide());
            _miLayers = new ToolStripMenuItem("Layers");
            _miLayers.DropDownOpening += (s, e) => BuildLayersMenu();

            _iopMenu.DropDownItems.AddRange(new ToolStripItem[]
            {
                _miConfig, _miReload, _miShowHide,
                new ToolStripSeparator(),
                _miLayers
            });

            InsertBeforeHelp(menu, _iopMenu);
            RefreshShowHideCaption();

            // try restore last selection (optional)
            if (Settings.Instance["iop_last_layer"] is string lastName &&
                Settings.Instance["iop_last_folder"] is string lastFolder &&
                Directory.Exists(lastFolder))
            {
                _layers[lastName] = new LayerInfo { Name = lastName, Folder = lastFolder, Visible = true };
                _activeLayer = lastName;
                RefreshShowHideCaption();
            }

            return true;
        }

        public override bool Exit()
        {
            var menu = Host.MainForm?
                .Controls
                .OfType<MenuStrip>()
                .FirstOrDefault() ?? Host.MainForm?.MainMenuStrip;

            if (menu != null && _iopMenu != null && menu.Items.Contains(_iopMenu))
                menu.Items.Remove(_iopMenu);

            return true;
        }

        // ---------- Menu builders / helpers ----------

        private void InsertBeforeHelp(MenuStrip menu, ToolStripMenuItem item)
        {
            var items = menu.Items.Cast<ToolStripItem>().ToList();
            var helpIdx = items.FindIndex(i => i.Text.Equals("Help", StringComparison.OrdinalIgnoreCase));
            if (helpIdx >= 0) menu.Items.Insert(helpIdx, item);
            else menu.Items.Add(item);
        }

        private void BuildLayersMenu()
        {
            _miLayers.DropDownItems.Clear();

            if (_layers.Count == 0)
            {
                _miLayers.DropDownItems.Add(new ToolStripMenuItem("(no layers)") { Enabled = false });
                _miLayers.DropDownItems.Add(new ToolStripSeparator());
            }
            else
            {
                foreach (var kv in _layers.OrderBy(k => k.Key))
                {
                    var li = kv.Value;
                    var nameItem = new ToolStripMenuItem(li.Name)
                    {
                        Checked = string.Equals(_activeLayer, li.Name, StringComparison.OrdinalIgnoreCase),
                        CheckOnClick = false
                    };
                    nameItem.Click += (s, e) =>
                    {
                        _activeLayer = li.Name;
                        RefreshShowHideCaption();
                    };

                    var visToggle = new ToolStripMenuItem(li.Visible ? "Hide" : "Show");
                    visToggle.Click += (s, e) =>
                    {
                        li.Visible = !li.Visible;
                        RefreshShowHideCaption();
                    };

                    nameItem.DropDownItems.Add(visToggle);
                    _miLayers.DropDownItems.Add(nameItem);
                }
                _miLayers.DropDownItems.Add(new ToolStripSeparator());
            }

            var add = new ToolStripMenuItem("Add Layer from Folder…");
            add.Click += (s, e) => AddLayerFromFolder();
            _miLayers.DropDownItems.Add(add);
        }

        private void RefreshShowHideCaption()
        {
            if (string.IsNullOrEmpty(_activeLayer) || !_layers.TryGetValue(_activeLayer, out var li))
            {
                _miShowHide.Text = "Show / Hide (no active layer)";
                _miShowHide.Enabled = false;
                return;
            }

            _miShowHide.Enabled = true;
            _miShowHide.Text = li.Visible ? "Hide (active layer)" : "Show (active layer)";
        }

        // ---------- Commands (GUI only) ----------

        private void DoConfig()
        {
            var dlg = new FolderBrowserDialog { Description = "Select image folder (no files will be loaded yet)" };
            if (dlg.ShowDialog(Host.MainForm) != DialogResult.OK) return;

            var folder = dlg.SelectedPath;
            var name = new DirectoryInfo(folder).Name;

            if (!InputBox("Layer name", "Enter a layer name", ref name)) return;

            _layers[name] = new LayerInfo { Name = name, Folder = folder, Visible = true };
            _activeLayer = name;

            Settings.Instance["iop_last_folder"] = folder;
            Settings.Instance["iop_last_layer"] = name;

            RefreshShowHideCaption();
            MessageBox.Show($"Configured layer \"{name}\"\nFolder: {folder}\n\n(Import comes in Step 2.)", "IOP");
        }

        private void AddLayerFromFolder()
        {
            var dlg = new FolderBrowserDialog { Description = "Select image folder (no files will be loaded yet)" };
            if (dlg.ShowDialog(Host.MainForm) != DialogResult.OK) return;

            var folder = dlg.SelectedPath;
            var name = new DirectoryInfo(folder).Name;
            if (!InputBox("Layer name", "Enter a layer name", ref name)) return;

            _layers[name] = new LayerInfo { Name = name, Folder = folder, Visible = true };
            _activeLayer = name;

            Settings.Instance["iop_last_folder"] = folder;
            Settings.Instance["iop_last_layer"] = name;

            RefreshShowHideCaption();
        }

        private void DoReloadStub()
        {
            if (string.IsNullOrEmpty(_activeLayer) || !_layers.TryGetValue(_activeLayer, out var li) || string.IsNullOrWhiteSpace(li.Folder))
            {
                MessageBox.Show("No active layer configured. Opening Config…", "IOP");
                DoConfig();
                return;
            }

            MessageBox.Show($"Would reload files from:\n{li.Folder}\n\n(Real import logic will be added in Step 2.)", "IOP");
        }

        private void DoShowHide()
        {
            if (string.IsNullOrEmpty(_activeLayer) || !_layers.TryGetValue(_activeLayer, out var li))
                return;

            li.Visible = !li.Visible;
            RefreshShowHideCaption();
        }

        // ---------- Helpers ----------

        private static bool InputBox(string title, string prompt, ref string value)
        {
            using (var form = new Form
            {
                Width = 420,
                Height = 150,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                Text = title,
                StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false,
                MaximizeBox = false
            })
            {
                var lbl = new Label { Left = 10, Top = 10, AutoSize = true, Text = prompt };
                var tb = new TextBox { Left = 10, Top = 35, Width = 380, Text = value ?? string.Empty };
                var ok = new Button { Text = "OK", Left = 220, Width = 80, Top = 70, DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "Cancel", Left = 310, Width = 80, Top = 70, DialogResult = DialogResult.Cancel };

                form.Controls.AddRange(new Control[] { lbl, tb, ok, cancel });
                form.AcceptButton = ok;
                form.CancelButton = cancel;

                var dr = form.ShowDialog();
                if (dr == DialogResult.OK)
                {
                    value = tb.Text;
                    return true;
                }
                return false;
            }
        }

        private sealed class LayerInfo
        {
            public string Name { get; set; }
            public string Folder { get; set; }
            public bool Visible { get; set; } = true;
        }
    }
}
