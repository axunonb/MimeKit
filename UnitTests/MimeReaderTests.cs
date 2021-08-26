﻿//
// MimeReaderTests.cs
//
// Author: Jeffrey Stedfast <jestedfa@microsoft.com>
//
// Copyright (c) 2013-2021 .NET Foundation and Contributors
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Globalization;
using System.Threading.Tasks;
using System.Collections.Generic;

using NUnit.Framework;

using Newtonsoft.Json;

using MimeKit;
using MimeKit.IO;
using MimeKit.Utils;
using MimeKit.IO.Filters;

namespace UnitTests {
	[TestFixture]
	public class MimeReaderTests
	{
		static readonly string MessagesDataDir = Path.Combine (TestHelper.ProjectDir, "TestData", "messages");
		static readonly string MboxDataDir = Path.Combine (TestHelper.ProjectDir, "TestData", "mbox");
		static FormatOptions UnixFormatOptions;

		public MimeReaderTests ()
		{
			UnixFormatOptions = FormatOptions.Default.Clone ();
			UnixFormatOptions.NewLineFormat = NewLineFormat.Unix;
		}

		[Test]
		public void TestArgumentExceptions ()
		{
			Assert.Throws<ArgumentNullException> (() => new MimeReader (null));
			Assert.Throws<ArgumentNullException> (() => new MimeReader (null, MimeFormat.Default));

			using (var stream = new MemoryStream ()) {
				var reader = new MimeReader (stream);

				Assert.Throws<ArgumentNullException> (() => reader.ReadEntity (null));
				Assert.ThrowsAsync<ArgumentNullException> (() => reader.ReadEntityAsync (null));

				Assert.Throws<ArgumentNullException> (() => reader.ReadMessage (null));
				Assert.ThrowsAsync<ArgumentNullException> (() => reader.ReadMessageAsync (null));
			}
		}

		static NewLineFormat DetectNewLineFormat (string fileName)
		{
			using (var stream = File.OpenRead (fileName)) {
				var buffer = new byte[1024];

				var nread = stream.Read (buffer, 0, buffer.Length);

				for (int i = 0; i < nread; i++) {
					if (buffer[i] == (byte) '\n') {
						if (i > 0 && buffer[i - 1] == (byte) '\r')
							return NewLineFormat.Dos;

						return NewLineFormat.Unix;
					}
				}
			}

			return NewLineFormat.Dos;
		}

		class MimeOffsets
		{
			[JsonProperty ("mimeType", NullValueHandling = NullValueHandling.Ignore)]
			public string MimeType { get; set; }

			[JsonProperty ("mboxMarkerOffset", NullValueHandling = NullValueHandling.Ignore)]
			public long? MboxMarkerOffset { get; set; }

			[JsonProperty ("lineNumber")]
			public int LineNumber { get; set; }

			[JsonProperty ("beginOffset")]
			public long BeginOffset { get; set; }

			[JsonProperty ("headersEndOffset")]
			public long HeadersEndOffset { get; set; }

			[JsonProperty ("endOffset")]
			public long EndOffset { get; set; }

			[JsonProperty ("message", NullValueHandling = NullValueHandling.Ignore)]
			public MimeOffsets Message { get; set; }

			[JsonProperty ("body", NullValueHandling = NullValueHandling.Ignore)]
			public MimeOffsets Body { get; set; }

			[JsonProperty ("children", NullValueHandling = NullValueHandling.Ignore)]
			public List<MimeOffsets> Children { get; set; }

			[JsonProperty ("octets")]
			public long Octets { get; set; }

			[JsonProperty ("lines", NullValueHandling = NullValueHandling.Ignore)]
			public int? Lines { get; set; }
		}

		enum MimeType
		{
			Message,
			MessagePart,
			Multipart,
			MimePart
		}

		class MimeItem
		{
			public readonly MimeOffsets Offsets;
			public readonly MimeType Type;

			public MimeItem (MimeType type, MimeOffsets offsets)
			{
				Offsets = offsets;
				Type = type;
			}
		}

		static void AssertMimeOffsets (MimeOffsets expected, MimeOffsets actual, int message, string partSpecifier)
		{
			Assert.AreEqual (expected.MimeType, actual.MimeType, $"mime-type differs for message #{message}{partSpecifier}");
			Assert.AreEqual (expected.MboxMarkerOffset, actual.MboxMarkerOffset, $"mbox marker begin offset differs for message #{message}{partSpecifier}");
			Assert.AreEqual (expected.BeginOffset, actual.BeginOffset, $"begin offset differs for message #{message}{partSpecifier}");
			Assert.AreEqual (expected.LineNumber, actual.LineNumber, $"begin line differs for message #{message}{partSpecifier}");
			Assert.AreEqual (expected.HeadersEndOffset, actual.HeadersEndOffset, $"headers end offset differs for message #{message}{partSpecifier}");
			Assert.AreEqual (expected.EndOffset, actual.EndOffset, $"end offset differs for message #{message}{partSpecifier}");
			Assert.AreEqual (expected.Octets, actual.Octets, $"octets differs for message #{message}{partSpecifier}");
			Assert.AreEqual (expected.Lines, actual.Lines, $"lines differs for message #{message}{partSpecifier}");

			if (expected.Message != null) {
				Assert.NotNull (actual.Message, $"message content is null for message #{message}{partSpecifier}");
				AssertMimeOffsets (expected.Message, actual.Message, message, partSpecifier + "/message");
			} else if (expected.Body != null) {
				Assert.NotNull (actual.Body, $"body content is null for message #{message}{partSpecifier}");
				AssertMimeOffsets (expected.Body, actual.Body, message, partSpecifier + "/0");
			} else if (expected.Children != null) {
				Assert.AreEqual (expected.Children.Count, actual.Children.Count, $"children count differs for message #{message}{partSpecifier}");
				for (int i = 0; i < expected.Children.Count; i++)
					AssertMimeOffsets (expected.Children[i], actual.Children[i], message, partSpecifier + $".{i}");
			}
		}

		class CustomMimeReader : MimeReader
		{
			public readonly List<MimeOffsets> Offsets = new List<MimeOffsets> ();
			public readonly List<MimeItem> stack = new List<MimeItem> ();
			long mboxMarkerBeginOffset = -1;
			int mboxMarkerLineNumber = -1;

			public CustomMimeReader (Stream stream, MimeFormat format) : base (stream, format)
			{
			}

			protected override void OnMboxMarkerRead (byte[] marker, int startIndex, int count, long beginOffset, int lineNumber, CancellationToken cancellationToken)
			{
				mboxMarkerBeginOffset = beginOffset;
				mboxMarkerLineNumber = lineNumber;

				base.OnMboxMarkerRead (marker, startIndex, count, beginOffset, lineNumber, cancellationToken);
			}

			protected override Task OnMboxMarkerReadAsync (byte[] marker, int startIndex, int count, long beginOffset, int lineNumber, CancellationToken cancellationToken)
			{
				OnMboxMarkerRead (marker, startIndex, count, beginOffset, lineNumber, cancellationToken);
				return base.OnMboxMarkerReadAsync (marker, startIndex, count, beginOffset, lineNumber, cancellationToken);
			}

			protected override void OnMimeMessageBegin (long beginOffset, int beginLineNumber, CancellationToken cancellationToken)
			{
				var offsets = new MimeOffsets {
					BeginOffset = beginOffset,
					LineNumber = beginLineNumber
				};

				if (stack.Count > 0) {
					var parent = stack[stack.Count - 1];
					Assert.AreEqual (MimeType.MessagePart, parent.Type);
					parent.Offsets.Message = offsets;
				} else {
					offsets.MboxMarkerOffset = mboxMarkerBeginOffset;
					Offsets.Add (offsets);
				}

				stack.Add (new MimeItem (MimeType.Message, offsets));

				base.OnMimeMessageBegin (beginOffset, beginLineNumber, cancellationToken);
			}

			protected override Task OnMimeMessageBeginAsync (long beginOffset, int beginLineNumber, CancellationToken cancellationToken)
			{
				OnMimeMessageBegin (beginOffset, beginLineNumber, cancellationToken);
				return base.OnMimeMessageBeginAsync (beginOffset, beginLineNumber, cancellationToken);
			}

			protected override void OnMimeMessageEnd (long beginOffset, int beginLineNumber, long headersEndOffset, long endOffset, int lines, CancellationToken cancellationToken)
			{
				var current = stack[stack.Count - 1];

				Assert.AreEqual (MimeType.Message, current.Type);

				current.Offsets.Octets = endOffset - headersEndOffset;
				current.Offsets.HeadersEndOffset = headersEndOffset;
				current.Offsets.EndOffset = endOffset;

				stack.RemoveAt (stack.Count - 1);

				base.OnMimeMessageEnd (beginOffset, beginLineNumber, headersEndOffset, endOffset, lines, cancellationToken);
			}

			protected override Task OnMimeMessageEndAsync (long beginOffset, int beginLineNumber, long headersEndOffset, long endOffset, int lines, CancellationToken cancellationToken)
			{
				OnMimeMessageEnd (beginOffset, beginLineNumber, headersEndOffset, endOffset, lines, cancellationToken);
				return base.OnMimeMessageEndAsync (beginOffset, beginLineNumber, headersEndOffset, endOffset, lines, cancellationToken);
			}

			void Push (MimeType type, ContentType contentType, long beginOffset, int beginLineNumber)
			{
				var offsets = new MimeOffsets {
					MimeType = contentType.MimeType,
					BeginOffset = beginOffset,
					LineNumber = beginLineNumber
				};

				if (stack.Count > 0) {
					var parent = stack[stack.Count - 1];

					switch (parent.Type) {
					case MimeType.Message:
						parent.Offsets.Body = offsets;
						break;
					case MimeType.Multipart:
						if (parent.Offsets.Children == null)
							parent.Offsets.Children = new List<MimeOffsets> ();
						parent.Offsets.Children.Add (offsets);
						break;
					default:
						Assert.Fail ();
						break;
					}
				} else {
					Offsets.Add (offsets);
				}

				stack.Add (new MimeItem (type, offsets));
			}

			void Pop (MimeType type, ContentType contentType, long beginOffset, int beginLineNumber, long headersEndOffset, long endOffset, int lines)
			{
				var current = stack[stack.Count - 1];

				Assert.AreEqual (type, current.Type);

				current.Offsets.Octets = endOffset - headersEndOffset;
				current.Offsets.HeadersEndOffset = headersEndOffset;
				current.Offsets.EndOffset = endOffset;
				current.Offsets.Lines = lines;

				stack.RemoveAt (stack.Count - 1);
			}

			protected override void OnMessagePartBegin (ContentType contentType, long beginOffset, int beginLineNumber, CancellationToken cancellationToken)
			{
				Push (MimeType.MessagePart, contentType, beginOffset, beginLineNumber);
				base.OnMessagePartBegin (contentType, beginOffset, beginLineNumber, cancellationToken);
			}

			protected override Task OnMessagePartBeginAsync (ContentType contentType, long beginOffset, int beginLineNumber, CancellationToken cancellationToken)
			{
				Push (MimeType.MessagePart, contentType, beginOffset, beginLineNumber);
				return base.OnMessagePartBeginAsync (contentType, beginOffset, beginLineNumber, cancellationToken);
			}

			protected override void OnMessagePartEnd (ContentType contentType, long beginOffset, int beginLineNumber, long headersEndOffset, long endOffset, int lines, CancellationToken cancellationToken)
			{
				Pop (MimeType.MessagePart, contentType, beginOffset, beginLineNumber, headersEndOffset, endOffset, lines);
				base.OnMessagePartEnd (contentType, beginOffset, beginLineNumber, headersEndOffset, endOffset, lines, cancellationToken);
			}

			protected override Task OnMessagePartEndAsync (ContentType contentType, long beginOffset, int beginLineNumber, long headersEndOffset, long endOffset, int lines, CancellationToken cancellationToken)
			{
				Pop (MimeType.MessagePart, contentType, beginOffset, beginLineNumber, headersEndOffset, endOffset, lines);
				return base.OnMessagePartEndAsync (contentType, beginOffset, beginLineNumber, headersEndOffset, endOffset, lines, cancellationToken);
			}

			protected override void OnMimePartBegin (ContentType contentType, long beginOffset, int beginLineNumber, CancellationToken cancellationToken)
			{
				Push (MimeType.MimePart, contentType, beginOffset, beginLineNumber);
				base.OnMimePartBegin (contentType, beginOffset, beginLineNumber, cancellationToken);
			}

			protected override Task OnMimePartBeginAsync (ContentType contentType, long beginOffset, int beginLineNumber, CancellationToken cancellationToken)
			{
				Push (MimeType.MimePart, contentType, beginOffset, beginLineNumber);
				return base.OnMimePartBeginAsync (contentType, beginOffset, beginLineNumber, cancellationToken);
			}

			protected override void OnMimePartEnd (ContentType contentType, long beginOffset, int beginLineNumber, long headersEndOffset, long endOffset, int lines, CancellationToken cancellationToken)
			{
				Pop (MimeType.MimePart, contentType, beginOffset, beginLineNumber, headersEndOffset, endOffset, lines);
				base.OnMimePartEnd (contentType, beginOffset, beginLineNumber, headersEndOffset, endOffset, lines, cancellationToken);
			}

			protected override Task OnMimePartEndAsync (ContentType contentType, long beginOffset, int beginLineNumber, long headersEndOffset, long endOffset, int lines, CancellationToken cancellationToken)
			{
				Pop (MimeType.MimePart, contentType, beginOffset, beginLineNumber, headersEndOffset, endOffset, lines);
				return base.OnMimePartEndAsync (contentType, beginOffset, beginLineNumber, headersEndOffset, endOffset, lines, cancellationToken);
			}

			protected override void OnMultipartBegin (ContentType contentType, long beginOffset, int beginLineNumber, CancellationToken cancellationToken)
			{
				Push (MimeType.Multipart, contentType, beginOffset, beginLineNumber);
				base.OnMultipartBegin (contentType, beginOffset, beginLineNumber, cancellationToken);
			}

			protected override Task OnMultipartBeginAsync (ContentType contentType, long beginOffset, int beginLineNumber, CancellationToken cancellationToken)
			{
				Push (MimeType.Multipart, contentType, beginOffset, beginLineNumber);
				return base.OnMultipartBeginAsync (contentType, beginOffset, beginLineNumber, cancellationToken);
			}

			protected override void OnMultipartEnd (ContentType contentType, long beginOffset, int beginLineNumber, long headersEndOffset, long endOffset, int lines, CancellationToken cancellationToken)
			{
				Pop (MimeType.Multipart, contentType, beginOffset, beginLineNumber, headersEndOffset, endOffset, lines);
				base.OnMultipartEnd (contentType, beginOffset, beginLineNumber, headersEndOffset, endOffset, lines, cancellationToken);
			}

			protected override Task OnMultipartEndAsync (ContentType contentType, long beginOffset, int beginLineNumber, long headersEndOffset, long endOffset, int lines, CancellationToken cancellationToken)
			{
				Pop (MimeType.Multipart, contentType, beginOffset, beginLineNumber, headersEndOffset, endOffset, lines);
				return base.OnMultipartEndAsync (contentType, beginOffset, beginLineNumber, headersEndOffset, endOffset, lines, cancellationToken);
			}
		}

		static void AssertMboxResults (string baseName, List<MimeOffsets> offsets, NewLineFormat newLineFormat)
		{
			var path = Path.Combine (MboxDataDir, baseName + "." + newLineFormat.ToString ().ToLowerInvariant () + "-offsets.json");
			var jsonSerializer = JsonSerializer.CreateDefault ();

			if (!File.Exists (path)) {
				jsonSerializer.Formatting = Formatting.Indented;

				using (var writer = new StreamWriter (path))
					jsonSerializer.Serialize (writer, offsets);
			}

			using (var reader = new StreamReader (path)) {
				var expectedOffsets = (List<MimeOffsets>) jsonSerializer.Deserialize (reader, typeof (List<MimeOffsets>));

				Assert.AreEqual (expectedOffsets.Count, offsets.Count, "message count");

				for (int i = 0; i < expectedOffsets.Count; i++)
					AssertMimeOffsets (expectedOffsets[i], offsets[i], i, string.Empty);
			}
		}

		void TestMbox (ParserOptions options, string baseName)
		{
			var mbox = Path.Combine (MboxDataDir, baseName + ".mbox.txt");
			NewLineFormat newLineFormat;
			List<MimeOffsets> offsets;

			using (var stream = File.OpenRead (mbox)) {
				var reader = new CustomMimeReader (stream, MimeFormat.Mbox);
				var format = FormatOptions.Default.Clone ();

				format.NewLineFormat = newLineFormat = DetectNewLineFormat (mbox);

				while (!reader.IsEndOfStream) {
					if (options != null)
						reader.ReadMessage (options);
					else
						reader.ReadMessage ();
				}

				offsets = reader.Offsets;
			}

			AssertMboxResults (baseName, offsets, newLineFormat);
		}

		async Task TestMboxAsync (ParserOptions options, string baseName)
		{
			var mbox = Path.Combine (MboxDataDir, baseName + ".mbox.txt");
			NewLineFormat newLineFormat;
			List<MimeOffsets> offsets;

			using (var stream = File.OpenRead (mbox)) {
				var reader = new CustomMimeReader (stream, MimeFormat.Mbox);
				var format = FormatOptions.Default.Clone ();

				format.NewLineFormat = newLineFormat = DetectNewLineFormat (mbox);

				while (!reader.IsEndOfStream) {
					if (options != null)
						await reader.ReadMessageAsync (options);
					else
						await reader.ReadMessageAsync ();
				}

				offsets = reader.Offsets;
			}

			AssertMboxResults (baseName, offsets, newLineFormat);
		}

		[Test]
		public void TestContentLengthMbox ()
		{
			var options = ParserOptions.Default.Clone ();
			options.RespectContentLength = true;

			TestMbox (options, "content-length");
		}

		[Test]
		public async Task TestContentLengthMboxAsync ()
		{
			var options = ParserOptions.Default.Clone ();
			options.RespectContentLength = true;

			await TestMboxAsync (options, "content-length");
		}

		[Test]
		public void TestJwzMbox ()
		{
			TestMbox (null, "jwz");
		}

		[Test]
		public async Task TestJwzMboxAsync ()
		{
			await TestMboxAsync (null, "jwz");
		}
	}
}