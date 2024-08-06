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
            Logger.Info("Start Crop down to " + CropPercentage + " Percent.");


            var patternTemplate = profileService.ActiveProfile.ImageFileSettings.GetFilePattern(e.Image.MetaData.Image.ImageType);
            var filepath = profileService.ActiveProfile.ImageFileSettings.FilePath;
            var imagePatterns = e.Image.GetImagePatterns();
            var ExistingFileName = Path.Combine(filepath, imagePatterns.GetImageFileString(patternTemplate) + ".fits");
            var newfilename = Path.GetDirectoryName(ExistingFileName) + @"\crop\" + Path.GetFileName(ExistingFileName);


            Logger.Info("New Filename:" + newfilename);

            string tmpfilename = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Logger.Info("Temp Filename:" + tmpfilename);

            var FileSaveInfo = new Image.FileFormat.FileSaveInfo {
                FilePath = tmpfilename,
                FileType = Core.Enum.FileTypeEnum.FITS,
                FilePattern = ""
            };

            if (!Directory.Exists(Path.GetDirectoryName(newfilename))) { Directory.CreateDirectory(Path.GetDirectoryName(newfilename)); }

            int LeftStart = (int)(e.Image.Properties.Width - e.Image.Properties.Width * CropPercentage) / 2;
            int TopStart = (int)(e.Image.Properties.Height - e.Image.Properties.Height * CropPercentage) / 2;
            int Width = (int)(e.Image.Properties.Width * CropPercentage);
            int Height = (int)(e.Image.Properties.Height * CropPercentage);



            IImageData CroppedImageData = Crop(e.Image, LeftStart, TopStart, Width, Height);

            await CroppedImageData.SaveToDisk(FileSaveInfo, dummyCancellation);

            File.Move(tmpfilename + ".fits", newfilename);

            return Task.CompletedTask;
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
            CropPercentage = 1600;
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
    }
}
