// 
// CSharpFoldingParser.cs
//  
// Author:
//       Mike Krüger <mkrueger@xamarin.com>
// 
// Copyright (c) 2012 Xamarin Inc.
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
using System;
using System.Collections.Generic;
using MonoDevelop.Ide.TypeSystem;
using MonoDevelop.Ide.Editor;

namespace MonoDevelop.CSharp.Parser
{
	unsafe class CSharpFoldingParser : IFoldingParser
	{
		#region IFoldingParser implementation

		static unsafe bool StartsIdentifier (byte* ptr, byte* endPtr, string identifier)
		{
			return identifier.UnsafeApply (
				(bytePtr) => {
					byte* startId = bytePtr.Value;
					byte* idPtr = startId;
					byte* endId = startId + identifier.Length;
					while (idPtr < endId) {
						if (ptr >= endPtr)
							return false;
						if (*idPtr != *ptr)
							return false;
						idPtr++;
						ptr++;
					}
					return true;
				},
				(charPtr) => {
					char* startId = charPtr.Value;
					char* idPtr = startId;
					char* endId = startId + identifier.Length;
					while (idPtr < endId) {
						if (ptr >= endPtr)
							return false;
						if (*idPtr != (char)*ptr)
							return false;
						idPtr++;
						ptr++;
					}
					return true;
				});
		}

		static unsafe bool StartsIdentifier (char* ptr, char* endPtr, string identifier)
		{
			return identifier.UnsafeApply (
				(bytePtr) => {
					byte* startId = bytePtr.Value;
					byte* idPtr = startId;
					byte* endId = startId + identifier.Length;
					while (idPtr < endId) {
						if (ptr >= endPtr)
							return false;
						if ((char)*idPtr != *ptr)
							return false;
						idPtr++;
						ptr++;
					}
					return true;
				},
				(charPtr) => {
					char* startId = charPtr.Value;
					char* idPtr = startId;
					char* endId = startId + identifier.Length;
					while (idPtr < endId) {
						if (ptr >= endPtr)
							return false;
						if (*idPtr != *ptr)
							return false;
						idPtr++;
						ptr++;
					}
					return true;
				});
		}

		static unsafe void SkipWhitespaces (ref byte* ptr, byte* endPtr, ref int column)
		{
			while (ptr < endPtr) {
				char ch = (char)*ptr;
				if (ch != ' ' && ch != '\t')
					return;
				column++;
				ptr++;
			}
		}

		static unsafe void SkipWhitespaces (ref char* ptr, char* endPtr, ref int column)
		{
			while (ptr < endPtr) {
				char ch = *ptr;
				if (ch != ' ' && ch != '\t')
					return;
				column++;
				ptr++;
			}
		}

		static unsafe string ReadToEol (string content, char* startPtr, ref char* ptr, char* endPtr, ref int line, ref int column)
		{
			char* lineBeginPtr = ptr;
			char* lineEndPtr = lineBeginPtr;

			while (ptr < endPtr) {
				switch (*ptr) {
				case '\n':
					if (lineEndPtr == lineBeginPtr)
						lineEndPtr = ptr;
					line++;
					column = 1;
					ptr++;
					return content.Substring ((int)(lineBeginPtr - startPtr), (int)(lineEndPtr - lineBeginPtr));
				case '\r':
					lineEndPtr = ptr;
					if (ptr + 1 < endPtr && *(ptr + 1) == '\n')
						ptr++;
					goto case '\n';
				}
				column++;
				ptr++;
			}
			return "";
		}

		static unsafe string ReadToEol (string content, byte* startPtr, ref byte* ptr, byte* endPtr, ref int line, ref int column)
		{
			byte* lineBeginPtr = ptr;
			byte* lineEndPtr = lineBeginPtr;

			while (ptr < endPtr) {
				switch ((char)*ptr) {
				case '\n':
					if (lineEndPtr == lineBeginPtr)
						lineEndPtr = ptr;
					line++;
					column = 1;
					ptr++;
					return content.Substring ((int)(lineBeginPtr - startPtr), (int)(lineEndPtr - lineBeginPtr));
				case '\r':
					lineEndPtr = ptr;
					if (ptr + 1 < endPtr && *(ptr + 1) == '\n')
						ptr++;
					goto case '\n';
				}
				column++;
				ptr++;
			}
			return "";
		}

		public unsafe ParsedDocument Parse (string fileName, string content)
		{
			var regionStack = new Stack<Tuple<string, DocumentLocation>> ();
			var result = new DefaultParsedDocument (fileName);
			bool inSingleComment = false, inMultiLineComment = false;
			bool inString = false, inVerbatimString = false;
			bool inChar = false;
			bool inLineStart = true, hasStartedAtLine = false;
			int line = 1, column = 1;
			var startLoc = DocumentLocation.Empty;
			
            content.UnsafeApply (
				(bytePtr) => {
					byte* startPtr = bytePtr.Value;
					byte* endPtr = startPtr + content.Length;
					byte* ptr = startPtr;
					byte* beginPtr = ptr;
					while (ptr < endPtr) {
						switch ((char)*ptr) {
						case '#':
							if (!inLineStart)
								break;
							inLineStart = false;
							ptr++;

							if (StartsIdentifier (ptr, endPtr, "region")) {
								var regionLocation = new DocumentLocation (line, column);
								column++;
								ptr += "region".Length;
								column += "region".Length;
								SkipWhitespaces (ref ptr, endPtr, ref column);
								regionStack.Push (Tuple.Create (ReadToEol (content, startPtr, ref ptr, endPtr, ref line, ref column), regionLocation));
								continue;
							} else if (StartsIdentifier (ptr, endPtr, "endregion")) {
								column++;
								ptr += "endregion".Length;
								column += "endregion".Length;
								if (regionStack.Count > 0) {
									var beginRegion = regionStack.Pop ();
									result.Add (new FoldingRegion (
													beginRegion.Item1, 
													new DocumentRegion (beginRegion.Item2.Line, beginRegion.Item2.Column, line, column),
													FoldType.UserRegion,
													true));
								}
								continue;
							} else {
								column++;
							}
							break;
						case '/':
							if (inString || inChar || inVerbatimString || inMultiLineComment || inSingleComment) {
								inLineStart = false;
								break;
							}
							if (ptr + 1 < endPtr) {
								char nextCh = (char)*(ptr + 1);
								if (nextCh == '/') {
									hasStartedAtLine = inLineStart;
									beginPtr = ptr + 2;
									startLoc = new DocumentLocation (line, column);
									ptr++;
									column++;
									inSingleComment = true;
								} else if (nextCh == '*') {
									hasStartedAtLine = inLineStart;
									beginPtr = ptr + 2;
									startLoc = new DocumentLocation (line, column);
									ptr++;
									column++;
									inMultiLineComment = true;
								}
							}
							inLineStart = false;
							break;
						case '*':
							inLineStart = false;
							if (inString || inChar || inVerbatimString || inSingleComment)
								break;
							if (inMultiLineComment && ptr + 1 < endPtr) {
								if (ptr + 1 < endPtr && (char)*(ptr + 1) == '/') {
									ptr += 2;
									column += 2;
									inMultiLineComment = false;
									result.Add (new MonoDevelop.Ide.TypeSystem.Comment () {
											Region = new DocumentRegion (startLoc, new DocumentLocation (line, column)),
												OpenTag = "/*",
												CommentType = MonoDevelop.Ide.TypeSystem.CommentType.Block,
												Text = content.Substring ((int)(beginPtr - startPtr), (int)(ptr - beginPtr)),
												CommentStartsLine = hasStartedAtLine
												});
									continue;
								}
							}
							break;
						case '@':
							inLineStart = false;
							if (inString || inChar || inVerbatimString || inSingleComment || inMultiLineComment)
								break;
							if (ptr + 1 < endPtr && (char)*(ptr + 1) == '"') {
								ptr++;
								column++;
								inVerbatimString = true;
							}
							break;
						case '\n':
							if (inSingleComment && hasStartedAtLine) {
								bool isDocumentation = (char)*beginPtr == '/';
								if (isDocumentation)
									beginPtr++;
							
								result.Add (new MonoDevelop.Ide.TypeSystem.Comment () { 
										Region = new DocumentRegion (startLoc, new DocumentLocation (line, column)),
											CommentType = MonoDevelop.Ide.TypeSystem.CommentType.SingleLine, 
											OpenTag = "//",
											Text = content.Substring ((int)(beginPtr - startPtr), (int)(ptr - beginPtr)),
											CommentStartsLine = hasStartedAtLine,
											IsDocumentation = isDocumentation
											});
								inSingleComment = false;
							}
							inString = false;
							inChar = false;
							inLineStart = true;
							line++;
							column = 1;
							ptr++;
							continue;
						case '\r':
							if (ptr + 1 < endPtr && (char)*(ptr + 1) == '\n')
								ptr++;
							goto case '\n';
						case '\\':
							if (inString || inChar)
								ptr++;
							break;
						case '"':
							if (inSingleComment || inMultiLineComment || inChar)
								break;
							if (inVerbatimString) {
								if (ptr + 1 < endPtr && (char)*(ptr + 1) == '"') {
									ptr++;
									column++;
									break;
								}
								inVerbatimString = false;
								break;
							}
							inString = !inString;
							break;
						case '\'':
							if (inSingleComment || inMultiLineComment || inString || inVerbatimString)
								break;
							inChar = !inChar;
							break;
						default:
							inLineStart &= (char)*ptr == ' ' || (char)*ptr == '\t';
							break;
						}

						column++;
						ptr++;
					}
				},
				(charPtr) => {
					char* startPtr = charPtr.Value;
					char* endPtr = startPtr + content.Length;
					char* ptr = startPtr;
					char* beginPtr = ptr;
					while (ptr < endPtr) {
						switch (*ptr) {
						case '#':
							if (!inLineStart)
								break;
							inLineStart = false;
							ptr++;

							if (StartsIdentifier (ptr, endPtr, "region")) {
								var regionLocation = new DocumentLocation (line, column);
								column++;
								ptr += "region".Length;
								column += "region".Length;
								SkipWhitespaces (ref ptr, endPtr, ref column);
								regionStack.Push (Tuple.Create (ReadToEol (content, startPtr, ref ptr, endPtr, ref line, ref column), regionLocation));
								continue;
							} else if (StartsIdentifier (ptr, endPtr, "endregion")) {
								column++;
								ptr += "endregion".Length;
								column += "endregion".Length;
								if (regionStack.Count > 0) {
									var beginRegion = regionStack.Pop ();
									result.Add (new FoldingRegion (
													beginRegion.Item1, 
													new DocumentRegion (beginRegion.Item2.Line, beginRegion.Item2.Column, line, column),
													FoldType.UserRegion,
													true));
								}
								continue;
							} else {
								column++;
							}
							break;
						case '/':
							if (inString || inChar || inVerbatimString || inMultiLineComment || inSingleComment) {
								inLineStart = false;
								break;
							}
							if (ptr + 1 < endPtr) {
								char nextCh = *(ptr + 1);
								if (nextCh == '/') {
									hasStartedAtLine = inLineStart;
									beginPtr = ptr + 2;
									startLoc = new DocumentLocation (line, column);
									ptr++;
									column++;
									inSingleComment = true;
								} else if (nextCh == '*') {
									hasStartedAtLine = inLineStart;
									beginPtr = ptr + 2;
									startLoc = new DocumentLocation (line, column);
									ptr++;
									column++;
									inMultiLineComment = true;
								}
							}
							inLineStart = false;
							break;
						case '*':
							inLineStart = false;
							if (inString || inChar || inVerbatimString || inSingleComment)
								break;
							if (inMultiLineComment && ptr + 1 < endPtr) {
								if (ptr + 1 < endPtr && *(ptr + 1) == '/') {
									ptr += 2;
									column += 2;
									inMultiLineComment = false;
									result.Add (new MonoDevelop.Ide.TypeSystem.Comment () {
											Region = new DocumentRegion (startLoc, new DocumentLocation (line, column)),
												OpenTag = "/*",
												CommentType = MonoDevelop.Ide.TypeSystem.CommentType.Block,
												Text = content.Substring ((int)(beginPtr - startPtr), (int)(ptr - beginPtr)),
												CommentStartsLine = hasStartedAtLine
												});
									continue;
								}
							}
							break;
						case '@':
							inLineStart = false;
							if (inString || inChar || inVerbatimString || inSingleComment || inMultiLineComment)
								break;
							if (ptr + 1 < endPtr && *(ptr + 1) == '"') {
								ptr++;
								column++;
								inVerbatimString = true;
							}
							break;
						case '\n':
							if (inSingleComment && hasStartedAtLine) {
								bool isDocumentation = *beginPtr == '/';
								if (isDocumentation)
									beginPtr++;
							
								result.Add (new MonoDevelop.Ide.TypeSystem.Comment () { 
										Region = new DocumentRegion (startLoc, new DocumentLocation (line, column)),
											CommentType = MonoDevelop.Ide.TypeSystem.CommentType.SingleLine, 
											OpenTag = "//",
											Text = content.Substring ((int)(beginPtr - startPtr), (int)(ptr - beginPtr)),
											CommentStartsLine = hasStartedAtLine,
											IsDocumentation = isDocumentation
											});
								inSingleComment = false;
							}
							inString = false;
							inChar = false;
							inLineStart = true;
							line++;
							column = 1;
							ptr++;
							continue;
						case '\r':
							if (ptr + 1 < endPtr && *(ptr + 1) == '\n')
								ptr++;
							goto case '\n';
						case '\\':
							if (inString || inChar)
								ptr++;
							break;
						case '"':
							if (inSingleComment || inMultiLineComment || inChar)
								break;
							if (inVerbatimString) {
								if (ptr + 1 < endPtr && *(ptr + 1) == '"') {
									ptr++;
									column++;
									break;
								}
								inVerbatimString = false;
								break;
							}
							inString = !inString;
							break;
						case '\'':
							if (inSingleComment || inMultiLineComment || inString || inVerbatimString)
								break;
							inChar = !inChar;
							break;
						default:
							inLineStart &= *ptr == ' ' || *ptr == '\t';
							break;
						}

						column++;
						ptr++;
					}
				});
			foreach (var fold in result.GetCommentsAsync().Result.ToFolds ()) {
				result.Add (fold);
			}
			return result;
		}
		#endregion
	}
}

