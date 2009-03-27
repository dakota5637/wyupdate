using System;
using System.IO;
using System.Net;
using System.Collections.Generic;
using System.Windows.Forms;
using System.Diagnostics;
using ICSharpCode.SharpZipLib.Checksums;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using wyUpdate.Common;

namespace wyUpdate.Downloader
{
    /// <summary>
    /// Downloads and resumes files from HTTP, FTP, and File (file://) URLS
    /// </summary>
    public class FileDownloader
    {
        // Block size to download is by default 4K.
        private const int BufferSize = 4096;

        // Determines whether the user has canceled or not.
        private volatile bool canceled = false;

        private string downloadingTo;

        /// <summary>
        /// This is the name of the file we get back from the server when we
        /// try to download the provided url. It will only contain a non-null
        /// string when we've successfully contacted the server and it has started
        /// sending us a file.
        /// </summary>
        public string DownloadingTo
        {
            get { return downloadingTo; }
        }

        //used to measure download speed
        private Stopwatch sw = new Stopwatch();
        private long sentSinceLastCalc = 0;
        private string downloadSpeed = "";

        // Usually a form or a winform control that implements "Invoke/BeginInvode"
        ContainerControl m_sender = null;
        // The delegate method (callback) on the sender to call
        Delegate m_senderDelegate = null;

        //download site and destination
        string url;
        List<string> urlList = new List<string>();
        string destFolder = "";

        bool waitingForResponse = false;

        public long Adler32;

        public bool UseRelativeProgress = false;

        public FileDownloader(List<string> urls, string downloadfolder, ContainerControl sender, Delegate senderDelegate)
        {
            urlList = urls;
            destFolder = downloadfolder;
            m_sender = sender;
            m_senderDelegate = senderDelegate;
        }

        public static void EnableLazySSL()
        {
            //Add a delegate that accepts all SSL's. Corrupt or not.
            ServicePointManager.ServerCertificateValidationCallback += new RemoteCertificateValidationCallback(OnCheckSSLCert);
        }

        private static bool OnCheckSSLCert(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            //allow all downloads regardless of SSL security errors
            /*   This will 'fix' the self-signed SSL certificate problem
               that's typical on most corporate intranets */
            return true;
        }

        public void Cancel()
        {
            this.canceled = true;
        }

        /// <summary>
        /// Download a file from a list or URLs. If downloading
        /// from one of the URLs fails, another URL is tried.
        /// </summary>
        public void Download()
        {
            // validate input
            if (urlList == null || urlList.Count == 0)
            {
                if (string.IsNullOrEmpty(url))
                {
                    //no sites specified, bail out
                    if (!canceled)
                        ThreadHelper.ReportError(m_sender, m_senderDelegate, string.Empty, new Exception("No download urls are specified."));

                    return;
                }
                else
                {
                    //single site specified, add it to the list
                    urlList = new List<string>();
                    urlList.Add(url);
                }
            }

            // try each url in the list until one suceeds

            bool allFailedWaitingForResponse = true;
            Exception ex = null;
            foreach (string s in urlList)
            {
                ex = null;
                try
                {
                    url = s;
                    BeginDownload();
                    ValidateDownload();
                }
                catch (Exception e)
                {
                    ex = e;

                    if (!waitingForResponse)
                        allFailedWaitingForResponse = false;
                }

                // If we got through that without an exception, we found a good url
                if (ex == null || canceled)
                {
                    allFailedWaitingForResponse = false;
                    break;
                }
            }


            /*
             If the all the sites failed before a response was recieved then either the 
             internet connection is shot, or the Proxy is shot. Either way it can't 
             hurt to try downloading without the proxy:
            */
            if (allFailedWaitingForResponse)
            {
                //try the sites again without the proxy
                WebRequest.DefaultWebProxy = null;

                foreach (string s in urlList)
                {
                    ex = null;
                    try
                    {
                        url = s;
                        BeginDownload();
                        ValidateDownload();
                    }
                    catch (Exception e)
                    {
                        ex = e;
                    }

                    // If we got through that without an exception, we found a good url
                    if (ex == null || canceled)
                        break;
                }
            }

            //Process complete (either sucessfully or failed), report back
            if (!canceled)
            {
                if (ex != null)
                    ThreadHelper.ReportError(m_sender, m_senderDelegate, string.Empty, ex);
                else
                    ThreadHelper.ReportSuccess(m_sender, m_senderDelegate, string.Empty);
            }
        }

        // Begin downloading the file at the specified url, and save it to the given folder.
        private void BeginDownload()
        {
            DownloadData data = null;
            FileStream fs = null;
            this.canceled = false;

            try
            {
                //start the stopwatch for speed calc
                sw.Start();

                // get download details 
                waitingForResponse = true;
                data = DownloadData.Create(url, destFolder);
                waitingForResponse = false;

                // Find out the name of the file that the web server gave us.
                string destFileName = Path.GetFileName(data.Response.ResponseUri.ToString());


                // The place we're downloading to (not from) must not be a URI,
                // because Path and File don't handle them...
                destFolder = destFolder.Replace("file:///", "").Replace("file://", "");
                this.downloadingTo = Path.Combine(destFolder, destFileName);

                if (!File.Exists(downloadingTo))
                {
                    // create the file
                    fs = File.Open(downloadingTo, FileMode.Create, FileAccess.Write);
                }
                else
                {
                    // apend to an existing file (resume)
                    fs = File.Open(downloadingTo, FileMode.Append, FileAccess.Write);
                }

                // create the download buffer
                byte[] buffer = new byte[BufferSize];

                int readCount;

                // update how many bytes have already been read
                sentSinceLastCalc = data.StartPoint; //for BPS calculation

                while ((int)(readCount = data.DownloadStream.Read(buffer, 0, BufferSize)) > 0)
                {
                    // break on cancel
                    if (canceled)
                    {
                        data.Close();
                        fs.Close();
                        break;
                    }

                    // update total bytes read
                    data.StartPoint += readCount;

                    // save block to end of file
                    SaveToFile(ref fs, ref buffer, readCount, this.downloadingTo);

                    //calculate download speed
                    calculateBps(data.StartPoint);

                    // send progress info
                    if (!canceled)
                    {
                        ThreadHelper.ReportProgress(m_sender, m_senderDelegate, downloadSpeed,
                            //use the realtive progress or the raw progress
                            UseRelativeProgress ? 
                                InstallUpdate.GetRelativeProgess(0, data.PercentDone) : 
                                data.PercentDone);
                    }

                    // break on cancel
                    if (canceled)
                    {
                        data.Close();
                        fs.Close();
                        break;
                    }
                }
            }
            catch (UriFormatException e)
            {
                throw new Exception(
                    String.Format("Could not parse the URL \"{0}\" - it's either malformed or is an unknown protocol.", url), e);
            }
            finally
            {
                if (data != null)
                    data.Close();
                if (fs != null)
                    fs.Close();
            }
        }

        private void SaveToFile(ref FileStream fs, ref byte[] buffer, int count, string fileName)
        {
            try
            {
                fs.Write(buffer, 0, count);
            }
            catch (Exception e)
            {
                throw new Exception(
                    String.Format("Error trying to save file \"{0}\": {1}", fileName, e.Message), e);
            }
        }

        private void calculateBps(long BytesReceived)
        {
            if (sw.Elapsed >= TimeSpan.FromSeconds(2))
            {
                sw.Stop();

                // Calculcate transfer speed.
                long bytes = BytesReceived - sentSinceLastCalc;
                double bps = bytes * 1000.0 / sw.Elapsed.TotalMilliseconds;
                downloadSpeed = BpsToString(bps);

                // Estimated seconds remaining based on the current transfer speed.
                //secondsRemaining = (int)((e.TotalBytesToReceive - e.BytesReceived) / bps);

                // Restart stopwatch for next second.
                sentSinceLastCalc = BytesReceived;
                sw.Reset();
                sw.Start();
            }
        }

        /// <summary>
        /// Constructs a download speed indicator string.
        /// </summary>
        /// <param name="bps">Bytes per second transfer rate.</param>
        /// <returns>String represenation of the transfer rate in bytes/sec, KB/sec, MB/sec, etc.</returns>
        private static string BpsToString(double bps)
        {
            string[] m = new string[] { "bytes", "KB", "MB", "GB", "TB", "PB", "EB", "ZB", "YB" };
            int i = 0;
            while (bps >= 0.9 * 1024)
            {
                bps /= 1024;
                i++;
            }

            return String.Format("{0:0.00} {1}/sec", bps, m[i]);
        }

        private void ValidateDownload()
        {
            if (!canceled && Adler32 != 0)
                ThreadHelper.ReportProgress(m_sender, m_senderDelegate, "Validating download...", -1);

            //if an Adler32 checksum is provided, check the file
            if (!canceled && Adler32 != 0 && Adler32 != GetAdler32(downloadingTo))
            {
                //file failed to vaildate, throw an error
                throw new Exception("The downloaded file failed the Adler32 validation.");
            }
        }

        private long GetAdler32(string fileName)
        {
            Adler32 adler = new Adler32();
            int totalComplete = 0;
            long fileSize = new FileInfo(fileName).Length;
            int sourceBytes;
            adler.Reset();

            FileStream fs = new FileStream(fileName, FileMode.Open);
            byte[] buffer = new byte[BufferSize];

            do
            {
                sourceBytes = fs.Read(buffer, 0, buffer.Length);
                totalComplete += sourceBytes;

                adler.Update(buffer, 0, sourceBytes);

                // break on cancel
                if (canceled)
                    break;

            } while (sourceBytes > 0);

            fs.Close();

            return adler.Value;
        }
    }


    class DownloadData
    {
        private WebResponse response;

        private Stream stream;
        private long size;
        private long start;

        public static DownloadData Create(string url, string destFolder)
        {
            // This is what we will return
            DownloadData downloadData = new DownloadData();

            
            WebRequest req = downloadData.GetRequest(url);
            try
            {
                downloadData.response = (WebResponse)req.GetResponse();
                downloadData.GetFileSize();
            }
            catch (Exception e)
            {
                throw new Exception(String.Format(
                    "Error downloading \"{0}\": {1}", url, e.Message), e);
            }

            // Check to make sure the response isn't an error. If it is this method
            // will throw exceptions.
            ValidateResponse(downloadData.response, url);

            // Take the name of the file given to use from the web server.
            String fileName = System.IO.Path.GetFileName(downloadData.response.ResponseUri.ToString());

            String downloadTo = Path.Combine(destFolder, fileName);

            // If we don't know how big the file is supposed to be,
            // we can't resume, so delete what we already have if something is on disk already.
            if (!downloadData.IsProgressKnown && File.Exists(downloadTo))
                File.Delete(downloadTo);

            if (downloadData.IsProgressKnown && File.Exists(downloadTo))
            {
                // We only support resuming on http requests
                if (!(downloadData.Response is HttpWebResponse))
                {
                    File.Delete(downloadTo);
                }
                else
                {
                    // Try and start where the file on disk left off
                    downloadData.start = new FileInfo(downloadTo).Length;

                    // If we have a file that's bigger than what is online, then something 
                    // strange happened. Delete it and start again.
                    if (downloadData.start > downloadData.size)
                        File.Delete(downloadTo);
                    else if (downloadData.start < downloadData.size)
                    {
                        // Try and resume by creating a new request with a new start position
                        downloadData.response.Close();
                        req = downloadData.GetRequest(url);
                        ((HttpWebRequest)req).AddRange((int)downloadData.start);
                        downloadData.response = req.GetResponse();

                        if (((HttpWebResponse)downloadData.Response).StatusCode != HttpStatusCode.PartialContent)
                        {
                            // They didn't support our resume request. 
                            File.Delete(downloadTo);
                            downloadData.start = 0;
                        }
                    }
                }
            }
            return downloadData;
        }

        // Used by the factory method
        private DownloadData()
        {
        }

        private DownloadData(WebResponse response, long size, long start)
        {
            this.response = response;
            this.size = size;
            this.start = start;
            this.stream = null;
        }

        /// <summary>
        /// Checks whether a WebResponse is an error.
        /// </summary>
        /// <param name="response"></param>
        private static void ValidateResponse(WebResponse response, string url)
        {
            if (response is HttpWebResponse)
            {
                HttpWebResponse httpResponse = (HttpWebResponse)response;
                // If it's an HTML page, it's probably an error page. Comment this
                // out to enable downloading of HTML pages.
                if (httpResponse.ContentType.Contains("text/html") || httpResponse.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new Exception(
                        String.Format("Could not download \"{0}\" - a web page was returned from the web server.",
                        url));
                }
            }
            else if (response is FtpWebResponse)
            {
                FtpWebResponse ftpResponse = (FtpWebResponse)response;
                if (ftpResponse.StatusCode == FtpStatusCode.ConnectionClosed)
                    throw new Exception(
                        String.Format("Could not download \"{0}\" - FTP server closed the connection.", url));
            }
            // FileWebResponse doesn't have a status code to check.
        }

        private void GetFileSize()
        {
            if (response != null)
            {
                try
                {
                    this.size = response.ContentLength;
                }
                catch (Exception) 
                {
                    //file size couldn't be determined
                    this.size = -1;
                }
            }
        }

        private WebRequest GetRequest(string url)
        {
            //WebProxy proxy = WebProxy.GetDefaultProxy();
            WebRequest request = WebRequest.Create(url);
            if (request is HttpWebRequest)
            {
                request.Credentials = CredentialCache.DefaultCredentials;
            }

            return request;
        }

        public void Close()
        {
            this.response.Close();
        }

        #region Properties
        public WebResponse Response
        {
            get { return response; }
            set { response = value; }
        }
        public Stream DownloadStream
        {
            get
            {
                if (this.start == this.size)
                    return Stream.Null;
                if (this.stream == null)
                    this.stream = this.response.GetResponseStream();
                return this.stream;
            }
        }

        public int PercentDone
        {
            get
            {
                if (size > 0)
                {
                    return (int)((start * 100) / size);
                }
                else
                    return 0;
            }
        }

        public long StartPoint
        {
            get { return this.start; }
            set { this.start = value; }
        }

        public bool IsProgressKnown
        {
            get
            {
                // If the size of the remote url is -1, that means we
                // couldn't determine it, and so we don't know
                // progress information.
                return this.size > -1;
            }
        }
        #endregion
    }
}