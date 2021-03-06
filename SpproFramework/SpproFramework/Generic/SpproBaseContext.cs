﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Reflection;
using System.Configuration;
using SpproFramework.Extensions;
using SpproFramework.Attributes;
using Microsoft.SharePoint.Client;
using System.IO;
using Microsoft.SharePoint.Client.UserProfiles;
using Microsoft.SharePoint.Client.Utilities;
using System.Net;

namespace SpproFramework.Generic
{
    public abstract class SpproBaseContext : IDisposable
    {
        //TO DO, THIS SHOULD NOT BE HERE. TESTING PURPOSES ONLY
        private static string AuthCookie { get; set; }

        #region Enums

        public enum AuthMode
        {
            Windows,
            Default
        }

        #endregion

        #region Static Members

        private static string GetSiteUrlFromConfig
        {
            get
            {
                return ConfigurationManager.AppSettings["SpUrl"];
            }
        }

        #endregion

        #region Private Members

        public ClientContext ClientContext { get; set; } //To do - make this private

        #endregion

        #region Constructors
        public SpproBaseContext(ClientContext clientContext, AuthMode authMode = AuthMode.Default)
        {
            this.ClientContext = clientContext;
            //Mixed authentication was causing problems so manually set to windows auth
            if (authMode == AuthMode.Windows)
            {
                ClientContext.ExecutingWebRequest += new EventHandler<WebRequestEventArgs>(Headers.MixedAuthentication.ctx_MixedAuthRequest);
            }

            var type = this.GetType();
            foreach (var property in type.GetProperties())
            {
                if (typeof(SpproBaseRepository).IsAssignableFrom(property.PropertyType))
                {

                    var listType = property.PropertyType; // typeof(SpproRepository<>);
                    var genericArgs = property.PropertyType.GetGenericArguments();
                    //var concreteType = listType.MakeGenericType(genericArgs);
                    var listName = genericArgs[0].Name;
                    var customAttributes = genericArgs[0].GetCustomAttributes(typeof(SpproListAttribute), true);
                    if (customAttributes.Length > 0 && !string.IsNullOrWhiteSpace(((SpproListAttribute)customAttributes[0]).ListName))
                    {
                        listName = ((SpproListAttribute)customAttributes[0]).ListName;
                    }
                    //var newRepo = Activator.CreateInstance(concreteType, listName, clientContext);
                    var newRepo = Activator.CreateInstance(listType, listName, clientContext);
                    property.SetValue(this, newRepo);
                }
            }
        }
        #endregion

        #region Private Methods

        /// <summary>
        /// Create Folder via recursion
        /// </summary>
        /// <param name="parentFolder"></param>
        /// <param name="fullFolderUrl"></param>
        /// <returns></returns>
        private Folder CreateFolderRecursive(Folder parentFolder, 
            string fullFolderUrl)
        {
            var folderUrls = fullFolderUrl.Split(new char[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            string folderUrl = folderUrls[0];
            var curFolder = parentFolder.Folders.Add(folderUrl);
            ClientContext.Web.Context.Load(curFolder);
            ClientContext.Web.Context.ExecuteQuery();

            if (folderUrls.Length > 1)
            {
                var subFolderUrl = string.Join("\\", folderUrls, 1, folderUrls.Length - 1);
                return CreateFolderRecursive(curFolder, subFolderUrl);
            }
            return curFolder;
        }

        #endregion

        #region Public Methods

        #region List File Helpers

        public List<Microsoft.SharePoint.Client.File> ListFiles(string libraryName, string folderName)
        {
            var docLib = ClientContext.Web.Lists.GetByTitle(libraryName);
            var camlQuery = new CamlQuery();
            camlQuery.ViewXml = @"<View Scope='Recursive'>
                                    <Query>
                                    </Query>
                                </View>";
            string folderUrl = string.Format("/{0}/{1}", libraryName, folderName);
            camlQuery.FolderServerRelativeUrl = folderUrl;
            ListItemCollection listItems = docLib.GetItems(camlQuery);
            ClientContext.Load(listItems);
            ClientContext.ExecuteQuery();
            return listItems.Select(a => a.File).ToList();
        }


        #endregion

        #region Download File Helpers

        /// <summary>
        /// Download image thumbnail using Web URL
        /// This is mostly used with Apps 
        /// </summary>
        /// <returns></returns>
        public byte[] DownloadThumbnail(string userName, string password, string relativeImageUrl)
        {
            if (!ClientContext.Web.IsObjectPropertyInstantiated("Url"))
            {
                ClientContext.Load(ClientContext.Web);
                ClientContext.ExecuteQuery();
            }
            var url = string.Format("{0}/_layouts/15/getpreview.ashx?path={1}", ClientContext.Web.Url, relativeImageUrl);
            var securePassword = password.ToSecureString(); //Convert to format that Office365 will accept
            var credentials = new SharePointOnlineCredentials(userName, securePassword);
            var authCookie = credentials.GetAuthenticationCookie(new Uri(url));
            WebClient client = new WebClient();
            client.Headers.Add(HttpRequestHeader.Cookie, authCookie);
            var data = client.DownloadData(url);
            return data;
        }

        /// <summary>
        /// Download image thumbnail using Web URL
        /// This is mostly used with Apps 
        /// Require web.config key="SpUserName" key="SpPassword" key="SpUrl" 
        /// </summary>
        /// <returns></returns>
        public async Task<byte[]> DownloadThumbnailAsync(string relativeImageUrl, int tries = 0)
        {
            try
            {
                var baseUrl = ConfigurationManager.AppSettings["SpUrl"];
                var url = string.Format("{0}/_layouts/15/getpreview.ashx?path={1}", baseUrl, relativeImageUrl);
                if (string.IsNullOrWhiteSpace(AuthCookie))
                {
                    var userName = ConfigurationManager.AppSettings["SpUserName"];
                    var password = ConfigurationManager.AppSettings["SpPassword"];
                    var securePassword = password.ToSecureString(); //Convert to format that Office365 will accept
                    var credentials = new SharePointOnlineCredentials(userName, securePassword);
                    SpproBaseContext.AuthCookie = credentials.GetAuthenticationCookie(new Uri(url));
                }

                try
                {
                    WebClient client = new WebClient();
                    client.Headers.Add(HttpRequestHeader.Cookie, SpproBaseContext.AuthCookie);
                    var data = await client.DownloadDataTaskAsync(new Uri(url));
                    return data;
                }
                catch (Exception ex)
                {
                    SpproBaseContext.AuthCookie = null;
                    if (++tries > 5)
                    {
                        throw new Exception("Too many tries to downloadThumbnail");
                    }
                }
                return await DownloadThumbnailAsync(relativeImageUrl, tries);
            }
            catch (Exception ex)
            {
                throw new Exception("Error downloading" + relativeImageUrl, ex);
            }
        }


        //public async Task<byte[]> Download()
        //{
        //    try
        //    {
        //        var baseUrl = ConfigurationManager.AppSettings["SpUrl"];
        //        var url = string.Format("{0}/_layouts/15/getpreview.ashx?path={1}", baseUrl, relativeImageUrl);
        //        if (string.IsNullOrWhiteSpace(AuthCookie))
        //        {
        //            var userName = ConfigurationManager.AppSettings["SpUserName"];
        //            var password = ConfigurationManager.AppSettings["SpPassword"];
        //            var securePassword = password.ToSecureString(); //Convert to format that Office365 will accept
        //            var credentials = new SharePointOnlineCredentials(userName, securePassword);
        //            SpproBaseContext.AuthCookie = credentials.GetAuthenticationCookie(new Uri(url));
        //        }

        //        try
        //        {
        //            WebClient client = new WebClient();
        //            client.Headers.Add(HttpRequestHeader.Cookie, SpproBaseContext.AuthCookie);
        //            var data = await client.DownloadDataTaskAsync(new Uri(url));
        //            return data;
        //        }
        //        catch (Exception ex)
        //        {
        //            SpproBaseContext.AuthCookie = null;
        //            if (++tries > 5)
        //            {
        //                throw new Exception("Too many tries to downloadThumbnail");
        //            }
        //        }
        //        return await DownloadThumbnailAsync(relativeImageUrl, tries);
        //    }
        //    catch (Exception ex)
        //    {
        //        throw new Exception("Error downloading" + relativeImageUrl, ex);
        //    }
        //}




        #endregion

        #region Upload File Helpers

        /// <summary>
        /// Uploads the specified file to a SharePoint site
        /// </summary>
        /// <param name="context">SharePoint Client Context</param>
        /// <param name="listTitle">List Title</param>
        /// <param name="fileName">File Name</param>
        public Microsoft.SharePoint.Client.File UploadFile(string fileName,
                               byte[] fileBytes,
                                Folder folder)
        {
            var fileUrl = String.Format("{0}/{1}", folder.ServerRelativeUrl, fileName);
            FileCreationInformation fci = new FileCreationInformation();
            fci.Content = fileBytes;
            fci.Url = fileUrl;
            fci.Overwrite = true;
            var uploadFile = folder.Files.Add(fci);
            ClientContext.Load(uploadFile);
            ClientContext.ExecuteQuery();
            return uploadFile;
        }

        public Microsoft.SharePoint.Client.File UploadLargeFile(
                                            string filename,
                                            byte[] file,
                                            Folder folder,
                                            int fileChunkSizeInMB = 3)
        {
            // Each sliced upload requires a unique ID.
            Guid uploadId = Guid.NewGuid();

            // File object.
            Microsoft.SharePoint.Client.File uploadFile;

            // Calculate block size in bytes.
            int blockSize = fileChunkSizeInMB * 1024 * 1024;

            // Get the size of the file.
            long fileSize = file.Length;

            if (fileSize <= blockSize)
            {
                // Use regular approach.
                using (MemoryStream fs = new MemoryStream(file))
                {
                    FileCreationInformation fileInfo = new FileCreationInformation();
                    fileInfo.ContentStream = fs;
                    fileInfo.Url = filename;
                    fileInfo.Overwrite = true;
                    uploadFile = folder.Files.Add(fileInfo);
                    ClientContext.Load(uploadFile);
                    ClientContext.ExecuteQuery();
                    // Return the file object for the uploaded file.
                    return uploadFile;
                }
            }
            else
            {
                // Use large file upload approach.
                ClientResult<long> bytesUploaded = null;

                MemoryStream ms = null;
                try
                {
                    ms = new MemoryStream(file);
                    using (BinaryReader br = new BinaryReader(ms))
                    {
                        byte[] buffer = new byte[blockSize];
                        Byte[] lastBuffer = null;
                        long fileoffset = 0;
                        long totalBytesRead = 0;
                        int bytesRead;
                        bool first = true;
                        bool last = false;

                        // Read data from file system in blocks. 
                        while ((bytesRead = br.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            totalBytesRead = totalBytesRead + bytesRead;

                            // You've reached the end of the file.
                            if (totalBytesRead == fileSize)
                            {
                                last = true;
                                // Copy to a new buffer that has the correct size.
                                lastBuffer = new byte[bytesRead];
                                Array.Copy(buffer, 0, lastBuffer, 0, bytesRead);
                            }

                            if (first)
                            {
                                using (MemoryStream contentStream = new MemoryStream())
                                {
                                    // Add an empty file.
                                    FileCreationInformation fileInfo = new FileCreationInformation();
                                    fileInfo.ContentStream = contentStream;
                                    fileInfo.Url = filename;
                                    fileInfo.Overwrite = true;
                                    uploadFile = folder.Files.Add(fileInfo);

                                    // Start upload by uploading the first slice. 
                                    using (MemoryStream s = new MemoryStream(buffer))
                                    {
                                        // Call the start upload method on the first slice.
                                        bytesUploaded = uploadFile.StartUpload(uploadId, s);
                                        ClientContext.ExecuteQuery();
                                        // fileoffset is the pointer where the next slice will be added.
                                        fileoffset = bytesUploaded.Value;
                                    }

                                    // You can only start the upload once.
                                    first = false;
                                }
                            }
                            else
                            {
                                // Get a reference to your file.
                                uploadFile = ClientContext.Web.GetFileByServerRelativeUrl(folder.ServerRelativeUrl + System.IO.Path.AltDirectorySeparatorChar + filename);

                                if (last)
                                {
                                    // Is this the last slice of data?
                                    using (MemoryStream s = new MemoryStream(lastBuffer))
                                    {
                                        // End sliced upload by calling FinishUpload.
                                        uploadFile = uploadFile.FinishUpload(uploadId, fileoffset, s);
                                        ClientContext.ExecuteQuery();

                                        // Return the file object for the uploaded file.
                                        return uploadFile;
                                    }
                                }
                                else
                                {
                                    using (MemoryStream s = new MemoryStream(buffer))
                                    {
                                        // Continue sliced upload.
                                        bytesUploaded = uploadFile.ContinueUpload(uploadId, fileoffset, s);
                                        ClientContext.ExecuteQuery();
                                        // Update fileoffset for the next slice.
                                        fileoffset = bytesUploaded.Value;
                                    }
                                }
                            }

                        } // while ((bytesRead = br.Read(buffer, 0, buffer.Length)) > 0)
                    }
                }
                finally
                {
                    if (ms != null)
                    {
                        ms.Dispose();
                    }
                }
            }

            return null;
        }

        #endregion

        #region Path File Helpers

        /// <summary>
        /// Get Folder
        /// Create if it doesnt exist
        /// </summary>
        /// <param name="listName"></param>
        /// <param name="folderName"></param>
        /// <returns></returns>
        public Folder CreateFolder(string listName, string folderName)
        {
            //Get Folder
            var list = ClientContext.Web.Lists.GetByTitle(listName);
            string targetFolderUrl = listName + folderName;
            var folder = ClientContext.Web.GetFolderByServerRelativeUrl(targetFolderUrl);
            ClientContext.Load(folder);

            try
            {
                ClientContext.ExecuteQuery();
                return folder;
            }
            catch (Exception ex)
            { }

            ClientContext.Load(list);
            ClientContext.ExecuteQuery();
            return CreateFolderRecursive(list.RootFolder, folderName);
        }

        #endregion

        #region User Helpers

        /// <summary>
        /// Get Current User, Dont execute now if you are executing other calls
        /// as ClientContext will bundle all call in 1.
        /// </summary>
        /// <param name="executeNow"></param>
        /// <returns></returns>
        public User CurrentUser(bool executeNow = true)
        {
            var spUser = ClientContext.Web.CurrentUser;
            ClientContext.Load(spUser);
            if (executeNow)
            {
                ClientContext.ExecuteQuery();
            }
            return spUser;
        }

        public User GetUserById(int id)
        {
            var user = ClientContext.Web.SiteUsers.GetById(id);
            ClientContext.Load(user);
            ClientContext.ExecuteQuery();
            return user;
        }

        public PersonProperties GetPeopleManagerProperties(string accountName)
        {
            var peopleManager = new PeopleManager(ClientContext);
            var userProperties = peopleManager.GetPropertiesFor(accountName);
            ClientContext.Load(userProperties);
            ClientContext.ExecuteQuery();
            return userProperties;

        }
        #endregion

        #endregion

        public void Dispose()
        {
            ClientContext.Dispose();
        }
    }
}
