﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using io.ebu.eis.canvasgenerator;
using io.ebu.eis.data.file;
using io.ebu.eis.data.ftp;
using io.ebu.eis.data.s3;
using io.ebu.eis.datastructures;
using io.ebu.eis.datastructures.Plain.Collections;
using io.ebu.eis.stomp;

namespace io.ebu.eis.shared
{
    [DataContract]
    public class ManagerImageReference : INotifyPropertyChanged
    {
        public CMConfigurationSection Config;

        // Serials for external generation handling
        public IImageGenerationHandler ImageGenerationHandler;
        public string Id;
        public long ExpectedSerial;
        public long LastUpdateSerial;

        public ManagerImageReference(CMConfigurationSection config, IImageGenerationHandler generationHandler)
        {
            TemplateFields = new DispatchedObservableCollection<ManagerTemplateField>();
            Config = config;
            IndexOffset = 0;
            ImageVariants = new List<ImageVariant>();
            ImageGenerationHandler = generationHandler;
        }


        private string _template;
        [DataMember(Name = "template")]
        public string Template { get { return _template; } set { _template = value; ReadTemplateFields(); OnPropertyChanged("Template"); } }


        private string _background;
        [DataMember(Name = "background")]
        public string Background { get { return _background; } set { _background = value; OnPropertyChanged("Background"); } }


        private string _link;
        [DataMember(Name = "link")]
        public string Link { get { return _link; } set { _link = value; OnPropertyChanged("Link"); } }

        private string _text;
        [DataMember(Name = "text", IsRequired = false)]
        public string Text { get { return _text; } set { _text = value; OnPropertyChanged("Text"); } }


        private List<ImageVariant> _imageVariants;
        [DataMember(Name = "imagevariants")]
        public List<ImageVariant> ImageVariants { get { return _imageVariants; } set { _imageVariants = value; OnPropertyChanged("ImageVariants"); } }


        private bool _isActive;
        [DataMember(Name = "isactive")]
        public bool IsActive { get { return _isActive; } set { _isActive = value; OnPropertyChanged("IsActive"); } }

        private int _indexOffset;
        [DataMember(Name = "indexoffset")]
        public int IndexOffset { get { return _indexOffset; } set { _indexOffset = value; OnPropertyChanged("IndexOffset"); } }

        private bool _canRepeate;
        [DataMember(Name = "canrepeate")]
        public bool CanRepeate { get { return _canRepeate; } set { _canRepeate = value; OnPropertyChanged("CanRepeate"); } }

        private int _itemsPerSlide = 4;
        [DataMember(Name = "itemsperslide", IsRequired = false)]
        public int ItemsPerSlide { get { return _itemsPerSlide; } set { _itemsPerSlide = value; OnPropertyChanged("ItemsPerSlide"); } }

        private bool _regenerateOnPublish;
        [DataMember(Name = "regenerateonpublish", IsRequired = false)]
        public bool RegenerateOnPublish { get { return _regenerateOnPublish; } set { _regenerateOnPublish = value; OnPropertyChanged("RegenerateOnPublish"); } }

        private TimeSpan _validityPeriod = TimeSpan.Zero;
        [DataMember(Name = "validityperiod", IsRequired = false)]
        public TimeSpan ValidityPeriod { get { return _validityPeriod; } set { _validityPeriod = value; OnPropertyChanged("ValidityPeriod"); } }

        private long _lastGenerationTime;
        public DateTime LastGenerationTime { get { return DateTime.FromBinary(_lastGenerationTime); } set { _lastGenerationTime = value.ToBinary(); OnPropertyChanged("LastGenerationTime"); OnPropertyChanged("LastGenerationTimeBinary"); } }
        [DataMember(Name = "lastgenerationtime")]
        public long LastGenerationTimeBinary { get { return _lastGenerationTime; } set { _lastGenerationTime = value; OnPropertyChanged("LastGenerationTime"); OnPropertyChanged("LastGenerationTimeBinary"); } }

        public bool IsValid
        {
            get
            {
                try
                {
                    return LastGenerationTime.Add(ValidityPeriod) > DateTime.Now;
                }
                catch (Exception) { }
                return false;
            }
        }

        private string _publicImageUrl;

        public string PublicImageUrl
        {
            get
            {
                if (String.IsNullOrEmpty(_publicImageUrl))
                {
                    PublicImageUrl = MakePublic();
                }
                return _publicImageUrl;
            }
            set
            {
                _publicImageUrl = value;
                OnPropertyChanged("PublicImageUrl");
            }
        }

        private DataMessage _context;
        [DataMember(Name = "context")]
        public DataMessage Context
        {
            get { return _context; }
            set
            {
                _context = value; ReadTemplateFields();
                OnPropertyChanged("Context");
            }
        }

        private DispatchedObservableCollection<ManagerTemplateField> _tpFields;
        [DataMember(Name = "templatefields")]
        public DispatchedObservableCollection<ManagerTemplateField> TemplateFields { get { return _tpFields; } set { _tpFields = value; OnPropertyChanged("TemplateFields"); } }


        private long _lastUsed;
        public DateTime LastUsed { get { return DateTime.FromBinary(_lastUsed); } set { _lastUsed = value.ToBinary(); OnPropertyChanged("LastUsed"); OnPropertyChanged("LastUsedBinary"); } }
        [DataMember(Name = "lastused")]
        public long LastUsedBinary { get { return _lastUsed; } set { _lastUsed = value; OnPropertyChanged("LastUsed"); OnPropertyChanged("LastUsedBinary"); } }

        private bool _rendering;
        private BitmapImage _previewImage;
        public BitmapImage PreviewImage
        {
            get
            {
                if (_previewImage == null)
                {
                    if (!_rendering)
                    {
                        _rendering = true;
                        var t = new Thread(Render);
                        t.Start();
                    }
                    // Return null so default dummy image is loaded while rendering image for preview
                    return null;
                }
                return _previewImage;
            }
            set
            {
                _previewImage = value;
                if (_previewImage != null)
                {
                    _rendering = false;
                    OnPropertyChanged("PreviewImage");
                    OnPropertyChanged("PreviewImageSource");
                }
            }
        }
        public ImageSource PreviewImageSource => PreviewImage;

        #region DispatchedUpdate

        public void DispatchedExternalPreviewUpdate(long newSerial, string newImageBase64)
        {
            // Update in Dispatcher
            Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Render,
              (SendOrPostCallback)delegate
              {
                  PreviewImage = HTMLRenderer.Base64ToImage(newImageBase64);
              }, null);
        }
        #endregion DispatchedUpdate


        #region Rendering

        public void ReRender(bool force)
        {
            // Initiate rerender by nulling out reference
            var changed = ReadTemplateFields();
            if (changed || force)
            {
                InvalidatePreviews();
                Console.WriteLine("RERENDERING by Invalidate on " + Template);
            }
        }

        private void InvalidatePreviews()
        {
            PreviewImage = null;
            OnPropertyChanged("PreviewImage");
            InvalidatePublic();
        }

        private void InvalidatePublic()
        {
            PublicImageUrl = null;
            OnPropertyChanged("PreviewImageSource");
        }

        public bool UpdateGlobal(DataMessage global)
        {
            if (Context == null)
            {
                Context = global.Clone();
                InvalidatePreviews();
                return true;
            }
            else
            {
                var changed = Context.MergeGlobal(global);
                return changed;
            }
        }

        public void ReRender(DataMessage global)
        {
            var changed = UpdateGlobal(global);
            if (changed)
                ReRender(false);
        }

        private void Render()
        {
            if (ImageGenerationHandler != null && Config.ImageGemerationConfiguration.EnableExternalImageGenerator)
            {
                // Use external Generator
                if (string.IsNullOrEmpty(Id))
                {
                    Id = Guid.NewGuid().ToString("D");
                }
                ExpectedSerial++;

                // Dispatch Generation Image
                ImageGenerationHandler.DispatchGeneration(Id, ExpectedSerial, this);

                // Register Callback for image return
                ImageGenerationHandler.RegisterImageCallback(Id, ExpectedSerial, this);
            }
            else
            {
                Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Render,
                    (SendOrPostCallback)delegate
                   {
                       PublicImageUrl = null;
                       PreviewImage =
                           HTMLRenderer.Base64ToImage(RenderImageWithTemplateContext(generationProperties: null));
                       _rendering = false;
                       LastGenerationTime = DateTime.Now;

                   }, null);
            }
        }
        public string RenderAndReturnBase64(string generationProperties = null)
        {
            _rendering = true;
            var image = RenderImageWithTemplateContext(generationProperties);

            Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Render,
            (SendOrPostCallback)delegate
            {
                PublicImageUrl = null;
                PreviewImage = HTMLRenderer.Base64ToImage(image);
                _rendering = false;
                LastGenerationTime = DateTime.Now;

            }, null);

            return image;
        }
        //public async void RenderAsync()
        //{
        //    PublicImageUrl = null;
        //    PreviewImage = await renderImageWithTemplateContextAsync();
        //}

        public void Destroy()
        {
            PreviewImage = null;
        }

        /// <summary>
        /// Read the templates variables
        /// </summary>
        /// <returns>Returns true if changes happened</returns>
        private bool ReadTemplateFields()
        {
            if (Template == null || TemplateFields == null)
            {
                return false;
            }
            if (!File.Exists(Template) && File.Exists(Path.Combine(Config.SlidesConfiguration.TemplatePath, Template)))
            {
                Template = Path.Combine(Config.SlidesConfiguration.TemplatePath, Template);
            }
            else if (!File.Exists(Template) && File.Exists(Path.Combine(Config.SlidesConfiguration.TemplatePath, Path.GetFileName(Template))))
            {
                Template = Path.Combine(Config.SlidesConfiguration.TemplatePath, Path.GetFileName(Template));
            }
            else if (!File.Exists(Template))
            {
                return false;
            }

            var templateHtml = File.ReadAllText(Template);

            // Pattern used to replace template variables
            string pattern = "@@(?<variable>.*?)(=(?<default>.*))?@@";

            var changes = false;
            if (TemplateFields == null)
                TemplateFields = new DispatchedObservableCollection<ManagerTemplateField>();
            var matches = Regex.Matches(templateHtml, pattern);
            foreach (Match m in matches)
            {
                var variable = m.Groups["variable"].Value;
                var defaultvalue = m.Groups["default"].Value;
                var isDefaultValue = false;
                string replaceValue = null;
                if (Context != null && Context.HasValue(variable))
                {
                    replaceValue = Context.GetValue(variable);
                }
                else if (!string.IsNullOrEmpty(defaultvalue))
                {
                    replaceValue = defaultvalue;
                    isDefaultValue = true;

                    //// Add to context if any
                    //if (Context != null)
                    //{
                    //    Context.Data.Add(new DataMessage()
                    //    {je 
                    //        Data = null,
                    //        DataType = "string",
                    //        Key = variable,
                    //        Value = defaultvalue
                    //    });
                    //}
                }

                // Add all fields to template fields
                if (TemplateFields.Any(x => x.Title == variable))
                {
                    // If already in there check if value changed
                    var existing = TemplateFields.First(x => x.Title == variable);
                    if (!isDefaultValue && String.Compare(existing.Value, replaceValue, StringComparison.Ordinal) != 0)
                    {
                        // Update value
                        if (replaceValue != null)
                        {
                            existing.Value = replaceValue;
                            changes = true;
                        }
                    }
                }
                else
                {
                    // Only add once to list
                    TemplateFields.Add(new ManagerTemplateField(variable, replaceValue));
                    changes = true;
                }
            }
            return changes;
        }

        //async Task<BitmapImage> renderImageWithTemplateContextAsync()
        //{
        //    //var filename = System.IO.Path.Combine(_config.SlidesConfiguration.TemplatePath, template);
        //    if (Template == null || TemplateFields == null)
        //    {
        //        return null;
        //    }
        //    if (!File.Exists(Template) && File.Exists(Path.Combine(_config.SlidesConfiguration.TemplatePath, Template)))
        //    {
        //        Template = Path.Combine(_config.SlidesConfiguration.TemplatePath, Template);
        //    }
        //    else if (!File.Exists(Template))
        //    {
        //        return null;
        //    }

        //    var templateHtml = File.ReadAllText(Template);

        //    foreach (Match m in Regex.Matches(templateHtml, pattern))
        //    {
        //        var variable = m.Groups[1].Value;
        //        var matchedValue = m.Value;
        //        var replaceField = TemplateFields.FirstOrDefault(x => x.Title == variable);
        //        var replaceValue = "";
        //        if (replaceField != null)
        //            replaceValue = replaceField.Value;
        //        templateHtml = templateHtml.Replace(matchedValue, replaceValue);
        //    }

        //    // Replace ##index## with offseted Values
        //    const string indexpattern = "##(.*?)##";
        //    foreach (Match m in Regex.Matches(templateHtml, indexpattern))
        //    {
        //        var variable = Convert.ToInt32(m.Groups[1].Value);
        //        var matchedValue = m.Value;
        //        var replaceValue = (IndexOffset + variable).ToString();
        //        templateHtml = templateHtml.Replace(matchedValue, replaceValue);
        //    }

        //    // Background Management
        //    const string bgpattern = "\"backgroundimage\": ?\"(?<image>.*?)\"";
        //    Match mbg = Regex.Match(templateHtml, bgpattern);
        //    if (mbg.Success && !String.IsNullOrEmpty(Background))
        //    {
        //        var variable = mbg.Groups[1].Value;
        //        var filepath = Background.Replace("\\", "/"); //"file://" +
        //        templateHtml = templateHtml.Replace(variable, filepath);
        //    }

        //    // Render the image
        //    var rendered = HTMLRenderer.RenderHtml(templateHtml, _config.SlidesConfiguration.TemplatePath);

        //    return rendered;
        //}

        private string RenderImageWithTemplateContext(string generationProperties)
        {
            //var filename = System.IO.Path.Combine(_config.SlidesConfiguration.TemplatePath, template);
            if (Template == null || TemplateFields == null)
            {
                return null;
            }
            if (!File.Exists(Template) && File.Exists(Path.Combine(Config.SlidesConfiguration.TemplatePath, Template)))
            {
                Template = Path.Combine(Config.SlidesConfiguration.TemplatePath, Template);
            }
            else if (!File.Exists(Template) && File.Exists(Path.Combine(Config.SlidesConfiguration.TemplatePath, Path.GetFileName(Template))))
            {
                Template = Path.Combine(Config.SlidesConfiguration.TemplatePath, Path.GetFileName(Template));
            }
            else if (!File.Exists(Template))
            {
                return null;
            }

            var templateHtml = File.ReadAllText(Template);

            // Pattern used to replace template variables
            string pattern = "@@(?<variable>.*?)(=(?<default>.*))?@@";

            foreach (Match m in Regex.Matches(templateHtml, pattern))
            {
                var variable = m.Groups["variable"].Value;
                var matchedValue = m.Value;
                var replaceField = TemplateFields.FirstOrDefault(x => x.Title == variable);
                var replaceValue = "";
                if (replaceField != null)
                    replaceValue = replaceField.Value;
                templateHtml = templateHtml.Replace(matchedValue, replaceValue);
            }

            // Replace ##index## with offseted Values
            const string indexpattern = "##(.*?)##";
            foreach (Match m in Regex.Matches(templateHtml, indexpattern))
            {
                var variable = Convert.ToInt32(m.Groups[1].Value);
                var matchedValue = m.Value;
                var replaceValue = (IndexOffset + variable).ToString();
                templateHtml = templateHtml.Replace(matchedValue, replaceValue);
            }

            // Background Management
            const string bgpattern = "\"backgroundimage\": ?\"(?<image>.*?)\"";
            Match mbg = Regex.Match(templateHtml, bgpattern);
            if (mbg.Success && !String.IsNullOrEmpty(Background))
            {
                var variable = mbg.Groups[1].Value;
                var filepath = Background.Replace("\\", "/"); //"file://" +
                var localRex = new Regex(@"^\w:\/+", RegexOptions.IgnoreCase);
                if (localRex.IsMatch(filepath))
                {
                    // Add Local file:/// prefix for path with Drive
                    filepath = "file:///" + filepath;
                }
                templateHtml = templateHtml.Replace(variable, filepath);
            }

            // Render the image
            var rendered = HTMLRenderer.RenderHtml(html: templateHtml,
                pathToWorkingDir: Config.SlidesConfiguration.TemplatePath,
                zoomFactorOptions: generationProperties);

            return rendered;
        }

        public void DispatchLastUpload()
        {
            foreach (UploadConfiguration outConf in Config.OutputConfiguration.UploadConfigurations)
            {
                try
                {
                    // Dispatch Message
                    foreach (DispatchConfiguration dispatch in outConf.DispatchConfigurations)
                    {
                        switch (dispatch.Type.ToUpper())
                        {
                            case "STOMP":
                                {
                                    var stompConnection = new StompTopicSender();
                                    stompConnection.SendStompImage(dispatch.StompUri, dispatch.StompUsername,
                                        dispatch.StompPassword, dispatch.StompTopic, PublicImageUrl, Text, Link);
                                }
                                break;

                            default:
                                {
                                    using (EventLog eventLog = new EventLog("Application"))
                                    {
                                        eventLog.Source = "Application";
                                        eventLog.WriteEntry($"EIS Content Manager failed to dispatch the image to an unknown service type {dispatch.Type}.", EventLogEntryType.Warning, 101, 1);
                                    }
                                }
                                break;

                        }
                    }
                }
                catch (Exception e)
                {
                    // Catch all exception to avoid crash and continue operations
                    using (EventLog eventLog = new EventLog("Application"))
                    {
                        eventLog.Source = "Application";
                        eventLog.WriteEntry($"EIS Content Manager failed to dispatch the image.\n{e.Message}\n\n{e.StackTrace}", EventLogEntryType.Error, 101, 1);
                    }
                }
            }
        }

        public static BitmapSource ResizeBitmapImage(BitmapImage image, int width, int height)
        {
            //using (MemoryStream strmImg = new MemoryStream())
            //{
            TransformedBitmap myBitmapImage = new TransformedBitmap();
            myBitmapImage.BeginInit();
            myBitmapImage.Source = image;
            ScaleTransform st = new ScaleTransform
            {
                ScaleX = (double)width / (double)image.PixelWidth,
                ScaleY = (double)height / (double)image.PixelHeight
            };
            myBitmapImage.Transform = st;
            myBitmapImage.EndInit();
            return myBitmapImage;
            //}
        }
        private string MakePublic()
        {
            // Reset Variants
            ImageVariants = new List<ImageVariant>(); // zFWx@-djeG6

            var delay = 0;
            while (PreviewImage == null && delay < 13)
            {
                // Implement waiting function when image should be generated first
                Thread.Sleep(250);
                delay++;
            }

            if (PreviewImage != null)
            {
                // Save image to temporary location
                foreach (ImageOutputConfiguration output in Config.OutputConfiguration.ImageOutputConfigurations)
                {
                    BitmapSource image = PreviewImage;
                    if (image.PixelWidth != output.Width || image.PixelHeight != output.Height)
                    {
                        // Resize original first for this output
                        image = ResizeBitmapImage(PreviewImage, output.Width, output.Height);
                    }

                    var imagePath = "";

                    if (output.Encoder == "PNG")
                    {
                        BitmapEncoder encoder = new PngBitmapEncoder();

                        imagePath = Path.Combine(Config.SlidesConfiguration.TemplatePath, Guid.NewGuid() + ".png");

                        encoder.Frames.Add(BitmapFrame.Create(image));
                        using (var filestream = new FileStream(imagePath, FileMode.Create))
                        {
                            encoder.Save(filestream);
                        }
                    }
                    if (output.Encoder == "JPEG")
                    {
                        //encoder = new JpegBitmapEncoder();
                        //((JpegBitmapEncoder)encoder).QualityLevel = output.Quality;
                        //extention = ".jpg";
                        // Compress IMAGE
                        var encoder = GetEncoder(ImageFormat.Jpeg);

                        // Create an Encoder object based on the GUID
                        // for the Quality parameter category.
                        Encoder myEncoder = Encoder.Compression;
                        Encoder myEncoder2 = Encoder.Quality;


                        // Create an EncoderParameters object.
                        // An EncoderParameters object has an array of EncoderParameter
                        // objects. In this case, there is only one
                        // EncoderParameter object in the array.
                        var myEncoderParameters = new EncoderParameters(2);

                        var myEncoderParameter = new EncoderParameter(myEncoder, 0L);
                        var myEncoderParameter2 = new EncoderParameter(myEncoder2, output.Quality);
                        myEncoderParameters.Param[0] = myEncoderParameter;
                        myEncoderParameters.Param[1] = myEncoderParameter2;

                        imagePath = Path.Combine(Config.SlidesConfiguration.TemplatePath, Guid.NewGuid().ToString() + ".jpg");

                        using (var outStream = new MemoryStream())
                        {
                            BitmapEncoder enc = new BmpBitmapEncoder();
                            enc.Frames.Add(BitmapFrame.Create(image));
                            enc.Save(outStream);
                            var bitmap = new Bitmap(outStream);

                            bitmap.Save(imagePath, encoder, myEncoderParameters);
                        }

                    }

                    // Upload Image to Amazon S3 and other Destination Uploads
                    var publicUrl = "";
                    foreach (UploadConfiguration outConf in Config.OutputConfiguration.UploadConfigurations)
                    {
                        try
                        {
                            var subFolder = outConf.Subfolder;
                            if (!output.IsDefault)
                            {
                                // Since not default, push to variant folder
                                subFolder = $"{subFolder}/{output.Name.ToLower()}";
                            }
                            switch (outConf.Type.ToUpper())
                            {
                                case "S3":
                                    {
                                        publicUrl = AWSS3Uploader.Upload(imagePath, outConf.AWSAccessKey,
                                            outConf.AWSSecretKey, outConf.S3BucketName, subFolder,
                                            outConf.PublicUriBase);

                                        if (!string.IsNullOrEmpty(outConf.LatestStaticImageName))
                                        {
                                            // We need a static image as well
                                            try
                                            {
                                                var latestStaticUrl = AWSS3Uploader.Upload(imagePath, outConf.AWSAccessKey,
                                                    outConf.AWSSecretKey, outConf.S3BucketName, subFolder,
                                                    outConf.PublicUriBase, outConf.LatestStaticImageName);
                                            }
                                            catch (Exception ex)
                                            {
                                                using (EventLog eventLog = new EventLog("Application"))
                                                {
                                                    eventLog.Source = "Application";
                                                    eventLog.WriteEntry($"EIS Content Manager failed to publish the image to AWS S3 static url.\n{ex.Message}\n\n{ex.StackTrace}", EventLogEntryType.Error, 101, 1);
                                                }
                                            }
                                        }
                                    }
                                    break;
                                case "FILE":
                                    {
                                        publicUrl = SystemFileUploader.Upload(imagePath, outConf.DestinationPath,
                                            outConf.PublicUriBase);
                                    }
                                    break;
                                case "FTP":
                                    {
                                        publicUrl = FtpFileUploader.Upload(imagePath, outConf.FtpServer,
                                            outConf.FtpUsername, outConf.FtpPassword, subFolder,
                                            outConf.UniqueFilename, outConf.PublicUriBase);
                                    }
                                    break;
                                default:
                                    {
                                        using (EventLog eventLog = new EventLog("Application"))
                                        {
                                            eventLog.Source = "Application";
                                            eventLog.WriteEntry($"EIS Content Manager failed to publish the image to an unknown destination type {outConf.Type}.", EventLogEntryType.Warning, 101, 1);
                                        }
                                    }
                                    break;
                            }
                        }
                        catch (Exception e)
                        {
                            // Catch all exception to avoid crash and continue operations
                            using (EventLog eventLog = new EventLog("Application"))
                            {
                                eventLog.Source = "Application";
                                eventLog.WriteEntry($"EIS Content Manager failed to publish the image with a general error.\n{e.Message}\n\n{e.StackTrace}", EventLogEntryType.Error, 101, 1);
                            }
                        }

                    }

                    // TODO Handle multiple Output PublicUrls, last one wins
                    // Add to variants
                    ImageVariants.Add(new ImageVariant() { Name = output.Name, Url = publicUrl });

                    // Save the Url of the IsDefault Image
                    if (output.IsDefault)
                    {
                        PublicImageUrl = publicUrl;
                    }

                    // Delete temporary Image
                    File.Delete(imagePath);
                }
            }
            if (string.IsNullOrEmpty(_publicImageUrl))
            {
                // TODO No default thus choose one
            }

            return _publicImageUrl;
        }

        private ImageCodecInfo GetEncoder(ImageFormat format)
        {
            ImageCodecInfo[] codecs = ImageCodecInfo.GetImageDecoders();
            return codecs.FirstOrDefault(codec => codec.FormatID == format.Guid);
        }

        #endregion Rendering

        public ManagerImageReference Clone(bool clonePreviewImage)
        {
            // Serialize
            var js = JsonSerializer.Serialize(this);
            // Deserialize
            var clone = JsonSerializer.Deserialize<ManagerImageReference>(js);
            // Reset instance values
            clone.Config = Config;
            clone.ImageGenerationHandler = ImageGenerationHandler;
            if (clonePreviewImage)
            {
                clone.PreviewImage = PreviewImage;
            }
            // Return
            return clone;
        }


        #region PropertyChanged
        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string propertyname)
        {
            if (PropertyChanged != null)
            {
                PropertyChanged(this, new PropertyChangedEventArgs(propertyname));
            }
        }
        #endregion PropertyChanged
    }
}
