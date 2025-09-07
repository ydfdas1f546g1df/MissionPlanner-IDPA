using MetadataExtractor;
using MissionPlanner.Plugin;
using MissionPlanner.Utilities;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using IO = System.IO; // <<< avoid ambiguity with MetadataExtractor.Directory
using System.Linq;
using System.Windows.Forms;

// GMap.NET
using GMap.NET;
using GMap.NET.MapProviders;
using GMap.NET.WindowsForms;

namespace ImageOverlayPlugin
{
    public class Plugin : MissionPlanner.Plugin.Plugin
    {
        public override string Name => "Image Overlay Plugin";
        public override string Version => "4.0.0";
        public override string Author => "IDPA";

        // UI
        private ToolStripMenuItem _menu;
        private ToolStripMenuItem _miOpenMap, _miReload, _miShowHide, _miLayers, _miZoom, _miOptions, _miOpenLog, _miDeleteActive, _miDeleteAll;

        // data
        private readonly Dictionary<string, LayerInfo> _layers = new Dictionary<string, LayerInfo>(StringComparer.OrdinalIgnoreCase);
        private string _activeLayer;

        // map window
        private MapWindow _mapWin;

        // options (persisted)
        private bool _drawImages;
        private bool _drawFootprints;
        private bool _drawCenters;
        private bool _allowHeuristics;
        private double _defaultSensorWidthMm;
        private double _defaultAGLm;
        private double _imageOpacity; // 0..1

        // logging
        private readonly string _logFile = IO.Path.Combine(IO.Path.GetTempPath(), "IOP.log");
        private static readonly log4net.ILog _alog = log4net.LogManager.GetLogger("ImageOverlayPlugin");
        private void Log(string msg)
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}";
            try { Console.WriteLine(line); } catch { }
            try { IO.File.AppendAllText(_logFile, line + Environment.NewLine); } catch { }
            try { _alog.Info(line); } catch { }
        }
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

        // ====== Mission Planner hooks ======

        public override bool Init() => true;

        public override bool Loaded()
        {
            var menu = Host.MainForm?.Controls.OfType<MenuStrip>().FirstOrDefault() ?? Host.MainForm?.MainMenuStrip;
            if (menu == null)
            {
                MessageBox.Show("Could not locate main menu strip.", "IOP");
                return true;
            }

            // read options
            _drawImages = Settings.Instance.GetBoolean("iop_draw_images", true);
            _drawFootprints = Settings.Instance.GetBoolean("iop_draw_footprints", true);
            _drawCenters = Settings.Instance.GetBoolean("iop_draw_centers", true);
            _allowHeuristics = Settings.Instance.GetBoolean("iop_allow_heuristics", false);
            _defaultSensorWidthMm = Settings.Instance.GetDouble("iop_sensor_width_mm", 6.0);
            _defaultAGLm = Settings.Instance.GetDouble("iop_default_agl_m", 60.0);
            _imageOpacity = Math.Min(1.0, Math.Max(0.0, Settings.Instance.GetDouble("iop_image_opacity", 0.7)));

            // restore last layer (optional)
            if (Settings.Instance["iop_last_layer"] is string lastName &&
                Settings.Instance["iop_last_folder"] is string lastFolder &&
                IO.Directory.Exists(lastFolder))
            {
                _layers[lastName] = new LayerInfo { Name = lastName, Folder = lastFolder, Visible = true };
                _activeLayer = lastName;
            }

            // build menu
            _menu = new ToolStripMenuItem("IOP");
            _miOpenMap = new ToolStripMenuItem("Open IOP Map…", null, (s, e) => OpenMapWindow());
            _miReload = new ToolStripMenuItem("Reload", null, (s, e) => DoReload());
            _miShowHide = new ToolStripMenuItem("Show / Hide", null, (s, e) => DoShowHide());
            _miLayers = new ToolStripMenuItem("Layers");
            _miLayers.DropDownOpening += (s, e) => BuildLayersMenu();
            _miZoom = new ToolStripMenuItem("Zoom to Active Layer", null, (s, e) => DoZoomActive());
            _miOptions = new ToolStripMenuItem("Options…", null, (s, e) => ShowOptions());
            _miOpenLog = new ToolStripMenuItem("Open Log…", null, (s, e) =>
            {
                try { System.Diagnostics.Process.Start("notepad.exe", _logFile); }
                catch { MessageBox.Show($"Log file:\n{_logFile}", "IOP"); }
            });
            _miDeleteActive = new ToolStripMenuItem("Delete Active Layer…", null, (s, e) => DeleteActiveLayer());
            _miDeleteAll = new ToolStripMenuItem("Remove ALL Layers…", null, (s, e) => DeleteAllLayers());

            _menu.DropDownItems.AddRange(new ToolStripItem[]
            {
                _miOpenMap,
                new ToolStripSeparator(),
                _miReload, _miShowHide, _miZoom,
                new ToolStripSeparator(),
                _miLayers,
                new ToolStripSeparator(),
                _miOptions, _miOpenLog,
                new ToolStripSeparator(),
                _miDeleteActive, _miDeleteAll
            });

            InsertBeforeHelp(menu, _menu);
            RefreshShowHideCaption();

            Log("Plugin loaded.");
            return true;
        }

        public override bool Exit()
        {
            try { if (_mapWin != null && !_mapWin.IsDisposed) _mapWin.Close(); } catch { }
            var menu = Host.MainForm?.Controls.OfType<MenuStrip>().FirstOrDefault() ?? Host.MainForm?.MainMenuStrip;
            if (menu != null && _menu != null && menu.Items.Contains(_menu))
                menu.Items.Remove(_menu);

            // dispose bitmaps
            foreach (var li in _layers.Values)
                foreach (var im in li.Images)
                    TryDisposeBitmap(im);

            Log("Plugin exited.");
            return true;
        }

        // ====== Menu helpers ======

        private static void InsertBeforeHelp(MenuStrip menu, ToolStripMenuItem item)
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
                        Log($"Active layer switched to '{li.Name}'.");
                        _mapWin?.Map?.Refresh();
                    };

                    var visToggle = new ToolStripMenuItem(li.Visible ? "Hide" : "Show");
                    visToggle.Click += (s, e) =>
                    {
                        li.Visible = !li.Visible;
                        RefreshShowHideCaption();
                        Log($"Layer '{li.Name}' visibility set to {li.Visible}.");
                        _mapWin?.Map?.Refresh();
                    };

                    var del = new ToolStripMenuItem("Delete…");
                    del.Click += (s, e) => DeleteLayer(li);

                    nameItem.DropDownItems.Add(visToggle);
                    nameItem.DropDownItems.Add(new ToolStripSeparator());
                    nameItem.DropDownItems.Add(del);

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
                _miZoom.Enabled = false;
                return;
            }

            _miShowHide.Enabled = true;
            _miZoom.Enabled = true;
            _miShowHide.Text = li.Visible ? "Hide (active layer)" : "Show (active layer)";
        }

        private void OpenMapWindow()
        {
            if (_mapWin == null || _mapWin.IsDisposed)
            {
                _mapWin = new MapWindow(this, Log);
                _mapWin.Show(Host.MainForm);
                Log("Map window created.");
            }
            else
            {
                _mapWin.Focus();
                Log("Map window focused.");
            }
            _mapWin.Map.Refresh();
        }

        // ====== Commands ======

        private void AddLayerFromFolder()
        {
            var dlg = new FolderBrowserDialog { Description = "Select image folder (EXIF GPS required for placement)" };
            if (dlg.ShowDialog(Host.MainForm) != DialogResult.OK) return;

            var folder = dlg.SelectedPath;
            var name = new IO.DirectoryInfo(folder).Name;
            if (!InputBox("Layer name", "Enter a layer name", ref name)) return;

            var li = new LayerInfo { Name = name, Folder = folder, Visible = true };
            _layers[name] = li;
            _activeLayer = name;

            Settings.Instance["iop_last_folder"] = folder;
            Settings.Instance["iop_last_layer"] = name;

            Log($"Layer added: '{name}' => {folder}");
            RefreshShowHideCaption();

            DoReload();
        }

        private void DoReload()
        {
            if (string.IsNullOrEmpty(_activeLayer) || !_layers.TryGetValue(_activeLayer, out var li) || string.IsNullOrWhiteSpace(li.Folder))
            {
                MessageBox.Show("No active layer configured. Please add a layer.", "IOP");
                Log("Reload aborted: no active layer.");
                return;
            }

            Log($"Reload started for layer '{li.Name}' from '{li.Folder}'.");
            LoadImages(li);
            BuildImageGeometryForLayer(li);

            MessageBox.Show($"Loaded {li.Images.Count} images from:\n{li.Folder}", "IOP");
            _mapWin?.Map?.Refresh();
        }

        private void DoShowHide()
        {
            if (string.IsNullOrEmpty(_activeLayer) || !_layers.TryGetValue(_activeLayer, out var li))
                return;

            li.Visible = !li.Visible;
            RefreshShowHideCaption();
            _mapWin?.Map?.Refresh();
        }

        private void DoZoomActive()
        {
            if (_mapWin == null || _mapWin.IsDisposed || _mapWin.Map == null)
            {
                MessageBox.Show("Open the IOP Map first.", "IOP");
                return;
            }
            if (string.IsNullOrEmpty(_activeLayer) || !_layers.TryGetValue(_activeLayer, out var li))
            {
                MessageBox.Show("No active layer.", "IOP");
                return;
            }
            ZoomToLayer(li);
        }

        private void ShowOptions()
        {
            using (var form = new Form
            {
                Width = 520,
                Height = 320,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                Text = "IOP Options",
                StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false,
                MaximizeBox = false
            })
            {
                int y = 10;
                var cbImg = new CheckBox { Left = 10, Top = y, Text = "Draw images", AutoSize = true, Checked = _drawImages }; y += 28;
                var cbFoot = new CheckBox { Left = 10, Top = y, Text = "Draw camera footprints (polygons)", AutoSize = true, Checked = _drawFootprints }; y += 28;
                var cbCent = new CheckBox { Left = 10, Top = y, Text = "Draw center crosses", AutoSize = true, Checked = _drawCenters }; y += 36;

                var lblOp = new Label { Left = 10, Top = y, Text = "Image opacity (0..1):", AutoSize = true };
                var tbOp = new TextBox { Left = 200, Top = y - 4, Width = 80, Text = _imageOpacity.ToString("0.##") }; y += 32;

                var cbHeur = new CheckBox { Left = 10, Top = y, Text = "Allow heuristics if EXIF missing (use defaults below)", AutoSize = true, Checked = _allowHeuristics }; y += 28;
                var lblSW = new Label { Left = 10, Top = y, Text = "Default sensor width (mm):", AutoSize = true };
                var tbSW = new TextBox { Left = 200, Top = y - 4, Width = 120, Text = _defaultSensorWidthMm.ToString("0.###") }; y += 28;
                var lblAGL = new Label { Left = 10, Top = y, Text = "Default AGL when unknown (m):", AutoSize = true };
                var tbAGL = new TextBox { Left = 200, Top = y - 4, Width = 120, Text = _defaultAGLm.ToString("0.#") }; y += 40;

                var ok = new Button { Text = "OK", Left = 320, Width = 80, Top = y, DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "Cancel", Left = 410, Width = 80, Top = y, DialogResult = DialogResult.Cancel };

                form.Controls.AddRange(new Control[] { cbImg, cbFoot, cbCent, lblOp, tbOp, cbHeur, lblSW, tbSW, lblAGL, tbAGL, ok, cancel });
                form.AcceptButton = ok;
                form.CancelButton = cancel;

                if (form.ShowDialog(Host.MainForm) == DialogResult.OK)
                {
                    _drawImages = cbImg.Checked;
                    _drawFootprints = cbFoot.Checked;
                    _drawCenters = cbCent.Checked;
                    _allowHeuristics = cbHeur.Checked;
                    if (double.TryParse(tbOp.Text, out var op)) _imageOpacity = Math.Min(1.0, Math.Max(0.0, op));
                    if (double.TryParse(tbSW.Text, out var sw)) _defaultSensorWidthMm = Math.Max(0.1, sw);
                    if (double.TryParse(tbAGL.Text, out var agl)) _defaultAGLm = Math.Max(1.0, agl);

                    Settings.Instance["iop_draw_images"] = _drawImages.ToString();
                    Settings.Instance["iop_draw_footprints"] = _drawFootprints.ToString();
                    Settings.Instance["iop_draw_centers"] = _drawCenters.ToString();
                    Settings.Instance["iop_allow_heuristics"] = _allowHeuristics.ToString();
                    Settings.Instance["iop_sensor_width_mm"] = _defaultSensorWidthMm.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    Settings.Instance["iop_default_agl_m"] = _defaultAGLm.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    Settings.Instance["iop_image_opacity"] = _imageOpacity.ToString(System.Globalization.CultureInfo.InvariantCulture);

                    // recompute footprints (heuristics may have changed)
                    foreach (var li in _layers.Values) BuildImageGeometryForLayer(li);
                    _mapWin?.Map?.Refresh();
                }
            }
        }

        // ====== Geometry & drawing ======

        private void BuildImageGeometryForLayer(LayerInfo li)
        {
            foreach (var im in li.Images)
            {
                im.Corners = TryComputeFootprintCorners(new PointLatLng(im.Latitude ?? 0, im.Longitude ?? 0), im);
            }
        }

        private List<PointLatLng> TryComputeFootprintCorners(PointLatLng center, ImageInfo img)
        {
            if (!img.Latitude.HasValue || !img.Longitude.HasValue)
                return null;

            double? f_mm = img.FocalLengthMm;
            double? h_m = img.AltitudeM;
            double? sensor_w_mm = img.SensorWidthMm;

            if ((!f_mm.HasValue || f_mm.Value <= 0) ||
                (!h_m.HasValue || h_m.Value <= 0) ||
                (!sensor_w_mm.HasValue || sensor_w_mm.Value <= 0))
            {
                if (!_allowHeuristics) return null;
                if (!f_mm.HasValue || f_mm.Value <= 0) f_mm = 24.0;
                if (!h_m.HasValue || h_m.Value <= 0) h_m = _defaultAGLm;
                if (!sensor_w_mm.HasValue || sensor_w_mm.Value <= 0) sensor_w_mm = _defaultSensorWidthMm;
            }

            double hFov_rad = 2.0 * Math.Atan((sensor_w_mm.Value * 0.5) / f_mm.Value);
            double ground_w_m = 2.0 * h_m.Value * Math.Tan(hFov_rad * 0.5);
            double aspect = 4.0 / 3.0; // assume if unknown
            double ground_h_m = ground_w_m / aspect;

            double yaw_deg = img.ImgDirectionDeg ?? 0.0;
            double yaw_rad = yaw_deg * Math.PI / 180.0;

            var half_w = ground_w_m / 2.0;
            var half_h = ground_h_m / 2.0;

            var local = new (double n, double e)[]
            {
                (+half_h, -half_w), // NW
                (+half_h, +half_w), // NE
                (-half_h, +half_w), // SE
                (-half_h, -half_w), // SW
            };

            var pts = new List<PointLatLng>(4);
            foreach (var p in local)
            {
                var n = p.n * Math.Cos(yaw_rad) - p.e * Math.Sin(yaw_rad);
                var e = p.n * Math.Sin(yaw_rad) + p.e * Math.Cos(yaw_rad);
                pts.Add(OffsetMeters(center, n, e));
            }
            return pts;
        }

        internal void DrawAll(Graphics g, GMapControl map)
        {
            if (g == null || map == null) return;

            var oldSmoothing = g.SmoothingMode;
            var oldInterp = g.InterpolationMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            foreach (var li in _layers.Values)
            {
                if (!li.Visible) continue;

                foreach (var im in li.Images)
                {
                    if (!im.Latitude.HasValue || !im.Longitude.HasValue) continue;

                    // centers
                    if (_drawCenters)
                    {
                        var pt = map.FromLatLngToLocal(new PointLatLng(im.Latitude.Value, im.Longitude.Value));
                        DrawCross(g, (float)pt.X, (float)pt.Y, 6, Pens.Red);
                    }

                    // compute corners if not cached
                    var corners = im.Corners;
                    if (corners == null)
                    {
                        corners = TryComputeFootprintCorners(new PointLatLng(im.Latitude.Value, im.Longitude.Value), im);
                        im.Corners = corners;
                    }
                    if (corners == null || corners.Count != 4) continue;

                    // polygons (footprints)
                    if (_drawFootprints)
                    {
                        var pts = corners.Select(p => map.FromLatLngToLocal(p)).Select(p => new PointF((float)p.X, (float)p.Y)).ToArray();
                        using (var fill = new SolidBrush(Color.FromArgb(40, Color.Red)))
                        using (var pen = new Pen(Color.FromArgb(160, Color.Red), 1.2f))
                        {
                            g.FillPolygon(fill, pts);
                            g.DrawPolygon(pen, pts);
                        }
                    }

                    // image overlay
                    if (_drawImages)
                    {
                        var ul = map.FromLatLngToLocal(corners[0]); // NW
                        var ur = map.FromLatLngToLocal(corners[1]); // NE
                        var ll = map.FromLatLngToLocal(corners[3]); // SW

                        if (EnsureBitmapLoaded(im))
                        {
                            using (var ia = new ImageAttributes())
                            {
                                var cm = new ColorMatrix
                                {
                                    Matrix00 = 1f,
                                    Matrix11 = 1f,
                                    Matrix22 = 1f,
                                    Matrix33 = (float)_imageOpacity,
                                    Matrix44 = 1f
                                };
                                ia.SetColorMatrix(cm, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);

                                var dest = new[]
                                {
                                    new PointF((float)ul.X, (float)ul.Y), // UL
                                    new PointF((float)ur.X, (float)ur.Y), // UR
                                    new PointF((float)ll.X, (float)ll.Y)  // LL
                                };

                                try
                                {
                                    g.DrawImage(im.BitmapRef, dest, new Rectangle(0, 0, im.BitmapRef.Width, im.BitmapRef.Height), GraphicsUnit.Pixel, ia);
                                }
                                catch (Exception ex)
                                {
                                    Log($"DrawImage '{im.Path}': {ex.Message}");
                                }
                            }
                        }
                    }
                }
            }

            g.SmoothingMode = oldSmoothing;
            g.InterpolationMode = oldInterp;
        }

        private static void DrawCross(Graphics g, float x, float y, float size, Pen pen)
        {
            g.DrawLine(pen, x - size, y, x + size, y);
            g.DrawLine(pen, x, y - size, x, y + size);
        }

        private static PointLatLng OffsetMeters(PointLatLng origin, double dNorth_m, double dEast_m)
        {
            const double R = 6378137.0; // WGS84
            double dLat = dNorth_m / R;
            double dLon = dEast_m / (R * Math.Cos(origin.Lat * Math.PI / 180.0));
            return new PointLatLng(
                origin.Lat + (dLat * 180.0 / Math.PI),
                origin.Lng + (dLon * 180.0 / Math.PI));
        }

        // ====== EXIF loading ======

        private void LoadImages(LayerInfo layer)
        {
            // dispose old bitmaps
            foreach (var im in layer.Images) TryDisposeBitmap(im);

            layer.Images.Clear();
            if (!IO.Directory.Exists(layer.Folder))
            {
                Log($"Folder does not exist: {layer.Folder}");
                return;
            }

            var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".tif", ".tiff" };

            int total = 0, withGps = 0, withoutGps = 0, errors = 0;

            foreach (var file in IO.Directory.GetFiles(layer.Folder))
            {
                if (!exts.Contains(IO.Path.GetExtension(file))) continue;
                total++;

                try
                {
                    var dirs = ImageMetadataReader.ReadMetadata(file);
                    var gps = dirs.OfType<MetadataExtractor.Formats.Exif.GpsDirectory>().FirstOrDefault();
                    var exif = dirs.OfType<MetadataExtractor.Formats.Exif.ExifSubIfdDirectory>().FirstOrDefault();

                    double? lat = null, lon = null, altM = null, focalMm = null, imgDir = null, sensorWmm = null;
                    var loc = gps?.GetGeoLocation();
                    if (loc != null && !loc.IsZero) { lat = loc.Latitude; lon = loc.Longitude; }

                    try
                    {
                        var altRat = gps?.GetRational(MetadataExtractor.Formats.Exif.GpsDirectory.TagAltitude);
                        if (altRat != null) altM = altRat.Value.ToDouble();
                    }
                    catch { }

                    try
                    {
                        var focRat = exif?.GetRational(MetadataExtractor.Formats.Exif.ExifDirectoryBase.TagFocalLength);
                        if (focRat != null) focalMm = focRat.Value.ToDouble();
                    }
                    catch { }

                    try
                    {
                        var dirRat = gps?.GetRational(MetadataExtractor.Formats.Exif.GpsDirectory.TagImgDirection);
                        if (dirRat != null) imgDir = dirRat.Value.ToDouble();
                    }
                    catch { }

                    // usually absent; heuristics can fill this if allowed
                    sensorWmm = null;

                    DateTime? shotTime = exif?.GetDateTime(MetadataExtractor.Formats.Exif.ExifDirectoryBase.TagDateTimeOriginal);

                    layer.Images.Add(new ImageInfo
                    {
                        Path = file,
                        Latitude = lat,
                        Longitude = lon,
                        AltitudeM = altM,
                        FocalLengthMm = focalMm,
                        ImgDirectionDeg = imgDir,
                        SensorWidthMm = sensorWmm,
                        ShotTime = shotTime
                    });

                    if (lat.HasValue && lon.HasValue) withGps++; else withoutGps++;
                }
                catch (Exception ex)
                {
                    errors++;
                    Log($"EXIF read error '{file}': {ex.Message}");
                }
            }

            Log($"Loaded from '{layer.Folder}': total={total}, withGPS={withGps}, withoutGPS={withoutGps}, errors={errors}");

            if (total > 0 && withGps == 0)
            {
                MessageBox.Show(
                    $"No GPS-tagged images found in:\n{layer.Folder}\n\nOverlay will be empty. Ensure EXIF GPS is present (JPEG/TIFF).",
                    "IOP", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private bool EnsureBitmapLoaded(ImageInfo im)
        {
            if (im.BitmapRef != null) return true;

            try
            {
                var bytes = IO.File.ReadAllBytes(im.Path);
                using (var ms = new IO.MemoryStream(bytes))
                    im.BitmapRef = new Bitmap(ms);
                return true;
            }
            catch (Exception ex)
            {
                Log($"Bitmap load failed '{im.Path}': {ex.Message}");
                return false;
            }
        }

        private void TryDisposeBitmap(ImageInfo im)
        {
            try { im.BitmapRef?.Dispose(); } catch { }
            im.BitmapRef = null;
        }

        // ====== Zoom ======

        private void ZoomToLayer(LayerInfo li)
        {
            if (_mapWin == null || _mapWin.IsDisposed || _mapWin.Map == null) return;

            var pts = new List<PointLatLng>();
            foreach (var im in li.Images)
            {
                if (im.Latitude.HasValue && im.Longitude.HasValue)
                    pts.Add(new PointLatLng(im.Latitude.Value, im.Longitude.Value));
                if (im.Corners != null) pts.AddRange(im.Corners);
            }

            if (pts.Count == 0) return;

            double minLat = pts.Min(p => p.Lat);
            double maxLat = pts.Max(p => p.Lat);
            double minLon = pts.Min(p => p.Lng);
            double maxLon = pts.Max(p => p.Lng);

            if (Math.Abs(maxLat - minLat) < 1e-9 && Math.Abs(maxLon - minLon) < 1e-9)
            {
                _mapWin.Map.Position = new PointLatLng(minLat, minLon);
                _mapWin.Map.Zoom = Math.Max(_mapWin.Map.MinZoom, Math.Min(18, _mapWin.Map.MaxZoom));
                return;
            }

            double padLat = (maxLat - minLat) * 0.15;
            double padLon = (maxLon - minLon) * 0.15;

            var rect = RectLatLng.FromLTRB(minLon - padLon, maxLat + padLat, maxLon + padLon, minLat - padLat);
            _mapWin.Map.SetZoomToFitRect(rect);
            _mapWin.Map.Position = new PointLatLng((minLat + maxLat) * 0.5, (minLon + maxLon) * 0.5);
            Log($"ZoomToLayer '{li.Name}': [{minLat},{minLon}] – [{maxLat},{maxLon}]");
        }

        // ====== Deletion ======

        private void DeleteActiveLayer()
        {
            if (string.IsNullOrEmpty(_activeLayer) || !_layers.TryGetValue(_activeLayer, out var li))
            {
                MessageBox.Show("No active layer.", "IOP");
                return;
            }
            if (MessageBox.Show($"Delete layer '{li.Name}'?", "IOP", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                DeleteLayer(li);
        }

        private void DeleteAllLayers()
        {
            if (_layers.Count == 0) return;
            if (MessageBox.Show("Remove ALL layers?", "IOP", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            foreach (var li in _layers.Values.ToList())
            {
                foreach (var im in li.Images) TryDisposeBitmap(im);
            }

            _layers.Clear();
            _activeLayer = null;
            RefreshShowHideCaption();
            _mapWin?.Map?.Refresh();
            Log("All layers removed.");
        }

        private void DeleteLayer(LayerInfo li)
        {
            foreach (var im in li.Images) TryDisposeBitmap(im);
            _layers.Remove(li.Name);

            if (string.Equals(_activeLayer, li.Name, StringComparison.OrdinalIgnoreCase))
                _activeLayer = _layers.Keys.FirstOrDefault();

            RefreshShowHideCaption();
            _mapWin?.Map?.Refresh();
            Log($"Layer '{li.Name}' deleted.");
        }

        // ====== Data types ======

        private sealed class LayerInfo
        {
            public string Name { get; set; }
            public string Folder { get; set; }
            public bool Visible { get; set; } = true;
            public List<ImageInfo> Images { get; } = new List<ImageInfo>();
        }

        private sealed class ImageInfo
        {
            public string Path { get; set; }
            public double? Latitude { get; set; }
            public double? Longitude { get; set; }
            public double? AltitudeM { get; set; }      // EXIF GPS altitude (approx.)
            public double? FocalLengthMm { get; set; }  // EXIF focal length
            public double? ImgDirectionDeg { get; set; }// EXIF GPS Img Direction (yaw)
            public double? SensorWidthMm { get; set; }  // often missing
            public DateTime? ShotTime { get; set; }

            public List<PointLatLng> Corners { get; set; } // NW, NE, SE, SW in lat/lon
            public Bitmap BitmapRef { get; set; }          // cached image
        }

        // ====== Map window (draw in Paint, not OnRender) ======

        private sealed class MapWindow : Form
        {
            public GMapControl Map { get; private set; }
            private readonly Plugin _owner;
            private readonly Action<string> _log;

            public MapWindow(Plugin owner, Action<string> logger)
            {
                _owner = owner;
                _log = logger ?? (_ => { });

                Text = "IOP Map";
                Width = 1200;
                Height = 800;
                StartPosition = FormStartPosition.CenterParent;

                Map = new GMapControl
                {
                    Dock = DockStyle.Fill,
                    DragButton = MouseButtons.Left,
                    ShowCenter = false,
                    MinZoom = 1,
                    MaxZoom = 20,
                    Zoom = 13
                };

                Map.Manager.Mode = AccessMode.ServerAndCache;
                Map.MapProvider = OpenStreetMapProvider.Instance;
                GMaps.Instance.Mode = AccessMode.ServerAndCache;

                // default to Schaffhausen area
                Map.Position = new PointLatLng(47.6973, 8.6349);

                // Draw our overlays in the map's Paint pass
                Map.Paint += Map_Paint;

                // Keep it repainted while moving/zooming
                Map.OnMapZoomChanged += () => Map.Invalidate();
                Map.OnPositionChanged += (pt) => Map.Invalidate();

                Controls.Add(Map);
            }

            private void Map_Paint(object sender, PaintEventArgs e)
            {
                try { _owner.DrawAll(e.Graphics, Map); }
                catch (Exception ex) { _log($"Map_Paint: {ex.Message}"); }
            }
        }
    }
}
