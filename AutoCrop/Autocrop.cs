using Autocrop.Properties;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Settings = Autocrop.Properties.Settings;

namespace NINA.Autocrop {
    /// <summary>
    /// This class exports the IPluginManifest interface and will be used for the general plugin information and options
    /// The base class "PluginBase" will populate all the necessary Manifest Meta Data out of the AssemblyInfo attributes. Please fill these accoringly
    /// 
    /// An instance of this class will be created and set as datacontext on the plugin options tab in N.I.N.A. to be able to configure global plugin settings
    /// The user interface for the settings will be defined by a DataTemplate with the key having the naming convention "Autocrop_Options" where Autocrop corresponds to the AssemblyTitle - In this template example it is found in the Options.xaml
    /// </summary>
    [Export(typeof(IPluginManifest))]
    public class Autocrop : PluginBase, INotifyPropertyChanged {
        private readonly IPluginOptionsAccessor pluginSettings;
        private readonly IProfileService profileService;
        private readonly IImageSaveMediator imageSaveMediator;
        private readonly IImageDataFactory imageDataFactory;
        private CancellationToken dummyCancellation = new CancellationToken();
        private List<IImageData> fitsImages = new List<IImageData>(); // Store the FITS images
        private DateTime firstImageTime; // Start time of first image
        private double aggregatorTime = 10; // Default aggregator time in seconds
        private double? firstImageDec;
        private double? firstImageRa;

        [ImportingConstructor]
        public Autocrop(IProfileService profileService, IOptionsVM options, IImageSaveMediator imageSaveMediator, IImageDataFactory imageDataFactory) {
            if (Settings.Default.UpdateSettings) {
                Settings.Default.Upgrade();
                Settings.Default.UpdateSettings = false;
                CoreUtil.SaveSettings(Settings.Default);
            }

            // This helper class can be used to store plugin settings that are dependent on the current profile
            this.pluginSettings = new PluginOptionsAccessor(profileService, Guid.Parse(this.Identifier));
            this.profileService = profileService;
            // React on a changed profile
            profileService.ProfileChanged += ProfileService_ProfileChanged;

            // Hook into image saving for adding FITS keywords or image file patterns
            this.imageSaveMediator = imageSaveMediator;
            this.imageDataFactory = imageDataFactory;


            // Run these handlers when an image is being saved
            this.imageSaveMediator.BeforeImageSaved += ImageSaveMediator_BeforeImageSaved;

        }

        public override Task Teardown() {
            // Make sure to unregister an event when the object is no longer in use. Otherwise garbage collection will be prevented.
            profileService.ProfileChanged -= ProfileService_ProfileChanged;
            imageSaveMediator.BeforeImageSaved -= ImageSaveMediator_BeforeImageSaved;

            return base.Teardown();
        }

        private void ProfileService_ProfileChanged(object sender, EventArgs e) {
            // Rase the event that this profile specific value has been changed due to the profile switch
            RaisePropertyChanged(nameof(ProfileSpecificNotificationMessage));
        }

        private async Task<Task> ImageSaveMediator_BeforeImageSaved(object sender, BeforeImageSavedEventArgs e) {
            // Directly reference and read the EXPOSURE header
            var exposureHeader = e.Image.MetaData.GenericHeaders.FirstOrDefault(h => h.Key == "EXPOSURE");

            double exposureTime = 0;
            DateTime observationTime;
            double ObjectRa, ObjectDec;


            if (exposureHeader != null && exposureHeader is DoubleMetaDataHeader doubleHeader) {
                // Cast the header to DoubleMetaDataHeader and access the value
                exposureTime = doubleHeader.Value; // Assuming Value is the exposure time as a double
                Logger.Info($"Exposure time: {exposureTime}");
            } else
            {
                Logger.Info("EXPOSURE header not found or not of expected type.");
                return Task.CompletedTask; 
            }


            var ObjectRaHeader = e.Image.MetaData.GenericHeaders.FirstOrDefault(h => h.Key == "OBJCTRA");
            if (ObjectRaHeader != null && ObjectRaHeader is StringMetaDataHeader stringObjectRaHeader) {
                ObjectRa = ConvertRAtoDegrees(stringObjectRaHeader.Value);
            } else {
                Logger.Info("OBJCTRA header not found or not of expected type.");
                return Task.CompletedTask;
            }
            var ObjectDecHeader = e.Image.MetaData.GenericHeaders.FirstOrDefault(h => h.Key == "OBJCTDEC");
            if (ObjectDecHeader != null && ObjectRaHeader is StringMetaDataHeader stringObjectDecHeader) {
                ObjectDec = ConvertDECtoDegrees(stringObjectDecHeader.Value);
            } else {
                Logger.Info("OBJCTDeC header not found or not of expected type.");
                return Task.CompletedTask;
            }

            var dateLocHeader = e.Image.MetaData.GenericHeaders.FirstOrDefault(h => h.Key == "DATE-LOC");
            if (dateLocHeader != null && dateLocHeader is StringMetaDataHeader stringHeader) {
                DateTime.TryParse(stringHeader.Value, out observationTime);
            } else {
                Logger.Info("DATE-LOC header not found or not of expected type.");
                return Task.CompletedTask;
            }

            // Convert DATE-LOC to local time
            TimeZoneInfo localTimeZone = TimeZoneInfo.Local;
            DateTimeOffset localTime = TimeZoneInfo.ConvertTime(observationTime, localTimeZone);
            DateTime localObservationTime = localTime.DateTime;
            Logger.Info("DATE-LOC is: " + localObservationTime);

            // Check if this is the first image being processed
            if (fitsImages.Count == 0) {
                firstImageTime = localObservationTime;  // First image, set the initial time
                firstImageRa = ObjectRa;
                firstImageDec = ObjectDec; 
            }

            if (firstImageRa != ObjectRa || firstImageDec != ObjectDec) {
                Logger.Info("Autocrop Notice: Slew detected, flushing image cache");
                firstImageTime = DateTime.Now;
                firstImageRa = null;
                firstImageDec = null;
                fitsImages.Clear();
                return Task.CompletedTask;
            }

            if (CropPercentage <= 0) {
                Logger.Info("Autocrop disabled due to Crop Percentage set to: " + CropPercentage + ".");
                return Task.CompletedTask;
            }

            int LeftStart = (int)(e.Image.Properties.Width - e.Image.Properties.Width * CropPercentage) / 2;
            int TopStart = (int)(e.Image.Properties.Height - e.Image.Properties.Height * CropPercentage) / 2;
            int Width = (int)(e.Image.Properties.Width * CropPercentage);
            int Height = (int)(e.Image.Properties.Height * CropPercentage);


            IImageData croppedImage = Crop(e.Image, LeftStart, TopStart, Width, Height);

            // Sum the pixels if it's not the first image
            if (fitsImages.Count > 0) {
                croppedImage = SumImages(fitsImages.Last(), croppedImage);
            }

            // Store the cropped image
            fitsImages.Add(croppedImage);

            // Calculate the elapsed time: start from the first image to the current image's time + exposure
            var elapsedTime = (localObservationTime - firstImageTime).TotalSeconds + exposureTime;

            if (elapsedTime > aggregatorTime) {
                Logger.Info("Time to save a crop")
                // Save the combined image when time exceeds aggregator time
                string finalFilename = GenerateFilename(e);
                if (!Directory.Exists(Path.GetDirectoryName(finalFilename))) { Directory.CreateDirectory(Path.GetDirectoryName(finalFilename)); }
                Logger.Info("New Filename:" + finalFilename);
                string tmpfilename = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                Logger.Info("Temp Filename:" + tmpfilename);

                var FileSaveInfo = new Image.FileFormat.FileSaveInfo {
                    FilePath = tmpfilename,
                    FileType = Core.Enum.FileTypeEnum.FITS,
                    FilePattern = ""
                };
                await croppedImage.SaveToDisk(FileSaveInfo, dummyCancellation);

                File.Move(tmpfilename + ".fits", finalFilename);

                // Reset the image list
                fitsImages.Clear();
            } else {
                Logger.Info("Adding image to crop cache");
            }

            return Task.CompletedTask;
        }

        // Modified SumImages method as requested
        private IImageData SumImages(IImageData baseImage, IImageData newImage) {
            // Ensure both images are the same size
            if (baseImage.Properties.Width != newImage.Properties.Width || baseImage.Properties.Height != newImage.Properties.Height) {
                throw new ArgumentException("Images must be of the same dimensions to sum.");
            }

            int pixelCount = baseImage.Properties.Width * baseImage.Properties.Height;
            ushort[] baseData = baseImage.Data.FlatArray;
            ushort[] newData = newImage.Data.FlatArray;

            ulong[] summedData = new ulong[pixelCount];

            // Sum the two images as ulong to avoid overflow
            for (int i = 0; i < pixelCount; i++) {
                summedData[i] = (ulong)baseData[i] + (ulong)newData[i];
            }

            // Find the minimum value in the summed array
            ulong minValue = summedData.Min();

            // Subtract the minimum value from each element and clip the result between 0 and 65535
            ushort[] resultData = new ushort[pixelCount];
            for (int i = 0; i < pixelCount; i++) {
                long adjustedValue = (long)(summedData[i] - minValue);  // Subtract minimum
                resultData[i] = (ushort)Math.Max(0, Math.Min(65535, adjustedValue));  // Clip to ushort range
            }

            // Create a new IImageData object with the processed pixel data
            return imageDataFactory.CreateBaseImageData(
                resultData, baseImage.Properties.Width, baseImage.Properties.Height, baseImage.Properties.BitDepth, baseImage.Properties.IsBayered, baseImage.MetaData);
        }

        private string GenerateFilename(BeforeImageSavedEventArgs e) {
            var patternTemplate = profileService.ActiveProfile.ImageFileSettings.GetFilePattern(e.Image.MetaData.Image.ImageType);
            var filepath = profileService.ActiveProfile.ImageFileSettings.FilePath;
            var imagePatterns = e.Image.GetImagePatterns();
            var ExistingFileName = Path.Combine(filepath, imagePatterns.GetImageFileString(patternTemplate) + ".fits");
            var newfilename = Path.GetDirectoryName(ExistingFileName) + @"\crop\" + Path.GetFileName(ExistingFileName);
            //return Path.Combine(filepath, imagePatterns.GetImageFileString(patternTemplate) + ".fits");
            return newfilename;
        }

        public IImageData Crop(IImageData sourceImage, int x, int y, int width, int height) {
            // Get the image properties
            var properties = sourceImage.Properties;
            int sourceWidth = properties.Width;
            int sourceHeight = properties.Height;

            // Validate the crop dimensions
            if (x < 0 || y < 0 || width <= 0 || height <= 0 ||
                x + width > sourceWidth || y + height > sourceHeight) {
                throw new ArgumentException("Invalid crop dimensions.");
            }

            // Create a new array to store the cropped pixel data
            ushort[] croppedData = new ushort[width * height];

            // Copy the pixel data from the source image to the cropped array
            for (int i = 0; i < height; i++) {
                int sourceOffset = (y + i) * sourceWidth + x;
                int destinationOffset = i * width;
                Array.Copy(sourceImage.Data.FlatArray, sourceOffset, croppedData, destinationOffset, width);
            }

            // Create a new IImageData object with the cropped pixel data
            IImageData croppedImage = imageDataFactory.CreateBaseImageData(
                croppedData, width, height, properties.BitDepth, properties.IsBayered, sourceImage.MetaData);

            return croppedImage;
        }
        private Task ImageSaveMediator_BeforeFinalizeImageSaved(object sender, BeforeFinalizeImageSavedEventArgs e) {
          
            return Task.CompletedTask;
        }

        public string DefaultNotificationMessage {
            get {
                return Settings.Default.DefaultNotificationMessage;
            }
            set {
                Settings.Default.DefaultNotificationMessage = value;
                CoreUtil.SaveSettings(Settings.Default);
                RaisePropertyChanged();
            }
        }

        public string ProfileSpecificNotificationMessage {
            get {
                return pluginSettings.GetValueString(nameof(ProfileSpecificNotificationMessage), string.Empty);
            }
            set {
                pluginSettings.SetValueString(nameof(ProfileSpecificNotificationMessage), value);
                RaisePropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void RaisePropertyChanged([CallerMemberName] string propertyName = null) {
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        private void InitializeOptions() {
            RaisePropertyChanged(nameof(CropPercentage));
        }

        public void ResetDefaults() {
            CropPercentage = 0.24;
            RaisePropertyChanged();

        }

        public double CropPercentage {
            get {
                return pluginSettings.GetValueDouble(nameof(CropPercentage), 0.1);
            }
            set {
                if (value > 1) { value = 1;}
                if (value < 0) { value = 0;}
                pluginSettings.SetValueDouble(nameof(CropPercentage), value);
                RaisePropertyChanged();
            }
        }

        public double AggregationTime {
            get  {
                return pluginSettings.GetValueDouble(nameof(AggregationTime), 0.1);
            } 
            set {
                if (value > 120) { value = 120; }
                if (value < 0) { value = 0; }
                pluginSettings.SetValueDouble(nameof(AggregationTime), value);
                //RaisePropertyChanged(); 
                RaisePropertyChanged(nameof(AggregationTime));
            }
        }

        // Convert RA in 'H M S' format to degrees
        public static double ConvertRAtoDegrees(string ra) {
            string[] raParts = ra.Split(' ');
            int hours = int.Parse(raParts[0]);
            int minutes = int.Parse(raParts[1]);
            int seconds = int.Parse(raParts[2]);

            // Convert RA to degrees: (hours + minutes/60 + seconds/3600) * 15
            double raDegrees = (hours + (minutes / 60.0) + (seconds / 3600.0)) * 15.0;
            return raDegrees;
        }

        // Convert Dec in 'D M S' format to degrees
        public static double ConvertDECtoDegrees(string dec) {
            string[] decParts = dec.Split(' ');
            int degrees = int.Parse(decParts[0], NumberStyles.AllowLeadingSign);
            int minutes = int.Parse(decParts[1]);
            int seconds = int.Parse(decParts[2]);

            // Convert Dec to degrees: degrees + minutes/60 + seconds/3600
            double decDegrees = Math.Abs(degrees) + (minutes / 60.0) + (seconds / 3600.0);

            // Apply the sign of degrees to the result
            if (degrees < 0) {
                decDegrees *= -1;
            }

            return decDegrees;
        }
    }
}
