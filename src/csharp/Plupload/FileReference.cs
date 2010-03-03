/**
 * FileReference.cs
 *
 * Copyright 2009, Moxiecode Systems AB
 * Released under GPL License.
 *
 * License: http://www.plupload.com/license
 * Contributing: http://www.plupload.com/contributing
 */

using System;
using System.IO;
using System.Threading;
using System.Windows.Threading;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows.Browser;
using System.Windows.Media.Imaging;
using FluxJpeg.Core.Encoder;
using FluxJpeg.Core;
using Plupload.PngEncoder;

namespace Moxiecode.Plupload {
	enum ImageType {
		Jpeg,
		Png
	}

	/// <summary>
	/// Description of File.
	/// </summary>
	public class FileReference {
		#region private fields
		private string name, uploadUrl, id;
		private FileInfo info;
		private SynchronizationContext syncContext;
		private int chunk, chunks, chunkSize;
		private bool cancelled;
        private long size;
        private Stream fileStream;
		#endregion

		/// <summary>Upload compleate delegate.</summary>
		public delegate void UploadCompleteHandler(object sender, UploadEventArgs e);

		/// <summary>Upload chunk compleate delegate.</summary>
		public delegate void UploadChunkCompleteHandler(object sender, UploadEventArgs e);

		/// <summary>Upload error delegate.</summary>
		public delegate void ErrorHandler(object sender, ErrorEventArgs e);

		/// <summary>Upload progress delegate.</summary>
		public delegate void ProgressHandler(object sender, ProgressEventArgs e);

		/// <summary>Upload complete event</summary>
		public event UploadCompleteHandler UploadComplete;

		/// <summary>Upload chunk complete event</summary>
		public event UploadChunkCompleteHandler UploadChunkComplete;

		/// <summary>Error event</summary>
		public event ErrorHandler Error;

		/// <summary>Progress event</summary>
		public event ProgressHandler Progress;

		/// <summary>
		///  Main constructor for the file reference.
		/// </summary>
		/// <param name="id">Unique file id for item.</param>
		/// <param name="info">FileInfo that got returned from a file selection.</param>
		public FileReference(string id, FileInfo info) {
			this.id = id;
			this.name = info.Name;
			this.info = info;
            this.size = info.Length;
		}

		/// <summary>Unique id for the file reference.</summary>
		public string Id {
			get { return id; }
		}

		/// <summary>File name to use with upload.</summary>
		public string Name {
			get { return name; }
			set { name = value; }
		}

		/// <summary>File size for the selected file.</summary>
		public long Size {
			get { return this.size; }
		}

		/// <summary>
		///  Uploads the file to the specific url and using the specified chunk_size.
		/// </summary>
		/// <param name="upload_url">URL to upload to.</param>
		/// <param name="chunk_size">Chunk size to use.</param>
        /// <param name="image_width">Image width to scale to.</param>
        /// <param name="image_height">Image height to scale to.</param>
        /// <param name="image_quality">Image quality to store as.</param>
		public void Upload(string upload_url, int chunk_size, int image_width, int image_height, int image_quality) {
			this.chunk = 0;
			this.chunkSize = chunk_size;
			this.chunks = (int) Math.Ceiling((double) this.Size / (double) chunk_size);
			this.cancelled = false;
			this.uploadUrl = upload_url;

            try {
                // Is jpeg and image size is defined
				if (Regex.IsMatch(this.name, @"\.(jpeg|jpg|png)$", RegexOptions.IgnoreCase) && (image_width != 0 || image_height != 0)) {
					if (Regex.IsMatch(this.name, @"\.png$"))
						this.fileStream = this.ResizeImage(this.info.OpenRead(), image_width, image_height, image_quality, ImageType.Png);
					else
						this.fileStream = this.ResizeImage(this.info.OpenRead(), image_width, image_height, image_quality, ImageType.Jpeg);

					this.fileStream.Seek(0, SeekOrigin.Begin);
					this.size = this.fileStream.Length;
				}  else
					this.fileStream = this.info.OpenRead();
            } catch (Exception ex) {
                syncContext.Send(delegate {
                    this.OnIOError(new ErrorEventArgs(ex.Message, 0, this.chunks));
                }, this);
            }

            this.UploadNextChunk();
		}

		/// <summary>
		///  Cancels the current upload.
		/// </summary>
		public void CancelUpload() {
			this.cancelled = true;
		}

		#region protected methods

		protected virtual void OnUploadComplete(UploadEventArgs e) {
			if (UploadComplete != null)
				UploadComplete(this, e);
		}

		protected virtual void OnUploadChunkComplete(UploadEventArgs e) {
			if (UploadChunkComplete != null)
				UploadChunkComplete(this, e);
		}

		protected virtual void OnIOError(ErrorEventArgs e) {
			if (Error != null)
				Error(this, e);
		}

		protected virtual void OnProgress(ProgressEventArgs e) {
			if (Progress != null)
				Progress(this, e);
		}

		#endregion

		#region private methods

		private void UploadNextChunk() {
			string url = this.uploadUrl;

			this.syncContext = SynchronizationContext.Current;

			url += url.IndexOf('?') == -1 ? '?' : '&';
			url += "&chunk=" + this.chunk;
			url += "&chunks=" + this.chunks;

			HttpWebRequest req = WebRequest.Create(new Uri(HtmlPage.Document.DocumentUri, url)) as HttpWebRequest;
			req.Method = "POST";

			IAsyncResult asyncResult = req.BeginGetRequestStream(new AsyncCallback(RequestStreamCallback), req);
		}

		private void RequestStreamCallback(IAsyncResult ar) {
			HttpWebRequest request = (HttpWebRequest) ar.AsyncState;

			request.ContentType = "application/octet-stream";

			Stream requestStream = null;
			byte[] buffer = new byte[4096];
			int bytes, loaded = 0, end;
			int percent, lastPercent = 0;

			try {
				// Move to start
				this.fileStream.Seek(this.chunk * this.chunkSize, SeekOrigin.Begin);
				loaded = this.chunk * this.chunkSize;

				// Find end
				end = (this.chunk + 1) * this.chunkSize;
				if (end > this.Size)
					end = (int) this.Size;

				requestStream = request.EndGetRequestStream(ar);

                while (loaded < end && (bytes = this.fileStream.Read(buffer, 0, end - loaded < buffer.Length ? end - loaded : buffer.Length)) != 0) {
					loaded += bytes;
					percent = (int) Math.Round((double) loaded / (double) this.Size * 100.0);

					if (percent > lastPercent) {
						syncContext.Send(delegate {
						    if (percent > lastPercent && !this.cancelled) {
						        this.OnProgress(new ProgressEventArgs(loaded, this.Size));
							    lastPercent = percent;
					        }
						}, this);
					}

					requestStream.Write(buffer, 0, bytes);
					requestStream.Flush();
				}
			} catch (Exception ex) {
				syncContext.Send(delegate {
					this.OnIOError(new ErrorEventArgs(ex.Message, this.chunk, this.chunks));
				}, this);
			} finally {
				try {
					if (requestStream != null)
	            		requestStream.Close();
				} catch (Exception ex) {
					syncContext.Send(delegate {
						this.OnIOError(new ErrorEventArgs(ex.Message, this.chunk, this.chunks));
					}, this);
				}

				try {
                    if (this.fileStream != null)
                        this.fileStream.Close();
				} catch (Exception ex) {
					syncContext.Send(delegate {
						this.OnIOError(new ErrorEventArgs(ex.Message, this.chunk, this.chunks));
					}, this);
				}
			}

			try {
				request.BeginGetResponse(new AsyncCallback(ResponseCallback), request);
			} catch (Exception ex) {
				syncContext.Send(delegate {
					this.OnIOError(new ErrorEventArgs(ex.Message, this.chunk, this.chunks));
				}, this);
			}
		}

		private void ResponseCallback(IAsyncResult ar) {
			try {
				HttpWebRequest request = ar.AsyncState as HttpWebRequest;

				WebResponse response = request.EndGetResponse(ar);

				syncContext.Post(ExtractResponse, response);
			} catch (Exception ex) {
				syncContext.Send(delegate {
					this.OnIOError(new ErrorEventArgs(ex.Message, this.chunk, this.chunks));
				}, this);
			}
		}

		private void ExtractResponse(object state) {
			HttpWebResponse response = state as HttpWebResponse;
			StreamReader respReader = null;
			Stream respStream = null;
			string content;

			try {
				respStream = response.GetResponseStream();

				if (response.StatusCode == HttpStatusCode.OK) {
					respReader = new StreamReader(respStream);

					if (respStream != null) {
						content = respReader.ReadToEnd();
					} else
						throw new Exception("Error could not open response stream.");
				} else
					throw new Exception("Error server returned status: " + ((int) response.StatusCode) + " " + response.StatusDescription);

				syncContext.Send(delegate {
					this.OnUploadChunkComplete(new UploadEventArgs(content, chunk, chunks));
				}, this);

				chunk++;

				if (chunk >= chunks) {
					syncContext.Send(delegate {
						this.OnUploadComplete(new UploadEventArgs(content, chunk, chunks));
					}, this);
				} else if (this.cancelled)
					this.UploadNextChunk();
			} catch (Exception ex) {
				syncContext.Send(delegate {
					this.OnIOError(new ErrorEventArgs(ex.Message, chunk, chunks));
				}, this);
			} finally {
				if (respStream != null)
					respStream.Close();

				if (respReader != null)
					respReader.Close();

				response.Close();
			}
		}

		private void Debug(string msg) {
			((ScriptObject) HtmlPage.Window.Eval("console")).Invoke("log", new string[] {msg});
		}

        private Stream ResizeImage(Stream image_stream, int width, int height, int quality, ImageType type) {
			try {
				// Load the image as a writeablebitmap
				WriteableBitmap writableBitmap;
				BitmapImage bitmapImage = new BitmapImage();
				bitmapImage.SetSource(image_stream);
				writableBitmap = new WriteableBitmap(bitmapImage);
				image_stream.Close();
				double scale = Math.Min((double) width / writableBitmap.PixelWidth, (double) height / writableBitmap.PixelHeight);

				// No resize needed
				if (scale >= 1.0)
					return image_stream;

				// Setup shorter names and pixelbuffers
				int w = writableBitmap.PixelWidth;
				int h = writableBitmap.PixelHeight;
				int[] p = writableBitmap.Pixels;
				byte[][,] imageRaster = new byte[3][,]; // RGB colors
				imageRaster[0] = new byte[w, h];
				imageRaster[1] = new byte[w, h];
				imageRaster[2] = new byte[w, h];

				// Copy WriteableBitmap data into buffer for FluxJpeg
				int i = 0;
				for (int y = 0; y < h; y++) {
					for (int x = 0; x < w; x++) {
						int color = p[i++];

						imageRaster[0][x, y] = (byte) (color >> 16); // R
						imageRaster[1][x, y] = (byte) (color >> 8);  // G
						imageRaster[2][x, y] = (byte) (color);       // B
					}
				}

				// Create new FluxJpeg image based on pixel data
				FluxJpeg.Core.Image jpegImage = new FluxJpeg.Core.Image(new ColorModel {
					colorspace = ColorSpace.RGB
				}, imageRaster);

				// Calc new proportional size
				width = (int) Math.Round(writableBitmap.PixelWidth * scale);
				height = (int) Math.Round(writableBitmap.PixelHeight * scale);

				// Resize the image
				ImageResizer resizer = new ImageResizer(jpegImage);
				Image resizedImage = resizer.Resize(width, height, FluxJpeg.Core.Filtering.ResamplingFilters.LowpassAntiAlias);
				Stream imageStream = new MemoryStream();

				if (type == ImageType.Jpeg) {
					// Encode the resized image as Jpeg
					JpegEncoder jpegEncoder = new JpegEncoder(resizedImage, quality, imageStream);
					jpegEncoder.Encode();
				} else {
					int[] pixelBuffer = new int[resizedImage.Height * resizedImage.Width];
					byte[][,] resizedRaster = resizedImage.Raster;

					// Convert FJCore raster to PixelBuffer
					for (int y = 0; y < resizedImage.Height; y++) {
						for (int x = 0; x < resizedImage.Width; x++) {
							int color = 0;

							color = color | resizedRaster[0][x, y] << 16; // R
							color = color | resizedRaster[1][x, y] << 8;  // G
							color = color | resizedRaster[2][x, y];       // B

							pixelBuffer[(y * resizedImage.Width) + x] = color;
						}
					}

					// Encode the resized image as Png
					PngEncoder pngEncoder = new PngEncoder(pixelBuffer, resizedImage.Width, resizedImage.Height, false, PngEncoder.FILTER_NONE, Deflater.BEST_COMPRESSION);
					byte[] pngBuffer = pngEncoder.pngEncode();
					imageStream.Write(pngBuffer, 0, pngBuffer.Length);
				}

				return imageStream;
			} catch {
				// Ignore the error and let the server resize the image
			}

			return image_stream;
        }

		#endregion
	}

	/// <summary>
	///  Upload event arguments class.
	/// </summary>
	public class UploadEventArgs : EventArgs {
		#region private fields
		private string response;
		private int chunk, chunks;
		#endregion

		/// <summary>
		///  Main constructor for the upload event.
		/// </summary>
		/// <param name="response">Response contents as a string.</param>
		public UploadEventArgs(string response) : this(response, 0, 0) {
		}

		/// <summary>
		///  Main constructor for the upload event.
		/// </summary>
		/// <param name="response">Response contents as a string.</param>
		/// <param name="chunk">Current chunk number.</param>
		/// <param name="chunks">Total chunks.</param>
		public UploadEventArgs(string response, int chunk, int chunks) {
			this.response = response;
			this.chunk = chunk;
			this.chunks = chunks;
		}

		/// <summary>Response from upload request.</summary>
		public string Response {
			get { return response; }
		}

		/// <summary>Chunk number.</summary>
		public int Chunk {
			get { return chunk; }
		}

		/// <summary>Total number of chunks.</summary>
		public int Chunks {
			get { return chunks; }
		}
	}

	/// <summary>
	///  Error event arguments class.
	/// </summary>
	public class ErrorEventArgs : EventArgs {
		#region private fields
		private string message;
		private int chunk, chunks;
		#endregion

		/// <summary>
		///  Main constructor for the error event.
		/// </summary>
		/// <param name="message">Error message.</param>
		public ErrorEventArgs(string message) : this(message, 0, 0) {
			this.message = message;
		}

		/// <summary>
		///  Main constructor for the error event.
		/// </summary>
		/// <param name="message">Error message.</param>
		/// <param name="chunk">Current chunk number.</param>
		/// <param name="chunks">Total chunks.</param>
		public ErrorEventArgs(string message, int chunk, int chunks) {
			this.message = message;
			this.chunk = chunk;
			this.chunks = chunks;
		}

		/// <summary>Chunk number.</summary>
		public int Chunk {
			get { return chunk; }
		}

		/// <summary>Total number of chunks.</summary>
		public int Chunks {
			get { return chunks; }
		}

		/// <summary>Error message.</summary>
		public string Message {
			get { return message; }
		}
	}

	/// <summary>
	///  Progress event arguments class.
	/// </summary>
	public class ProgressEventArgs : EventArgs {
		#region private fields
		private long loaded, total;
		#endregion

		/// <summary>
		///  Main constructor for the progress events args.
		/// </summary>
		/// <param name="loaded">Number of bytes uploaded.</param>
		/// <param name="total">Total bytes to upload.</param>
		public ProgressEventArgs(long loaded, long total) {
			this.loaded = loaded;
			this.total = total;
		}

		/// <summary>Total bytes to upload.</summary>
		public long Total {
			get { return total; }
		}

		/// <summary>Number of bytes upload so far.</summary>
		public long Loaded {
			get { return loaded; }
		}
	}
}
