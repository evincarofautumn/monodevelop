//
// TextEditorViewContent.cs
//
// Author:
//       Mike Krüger <mkrueger@xamarin.com>
//
// Copyright (c) 2014 Xamarin Inc. (http://xamarin.com)
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
using MonoDevelop.Ide.Gui;
using MonoDevelop.Projects;
using MonoDevelop.Ide.Gui.Content;
using MonoDevelop.Components.Commands;
using MonoDevelop.Ide.Commands;
using System.Collections;
using System.Collections.Generic;
using MonoDevelop.Ide.TypeSystem;
using System.IO;
using MonoDevelop.Core.Text;
using System.Text;
using Gtk;
using ICSharpCode.NRefactory.TypeSystem;
using System.Linq;
using MonoDevelop.Ide.Editor.Extension;
using ICSharpCode.NRefactory.Refactoring;
using MonoDevelop.Components;
using MonoDevelop.Core;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.CodeAnalysis;
using Gdk;
using MonoDevelop.Ide.CodeFormatting;
using System.Collections.Immutable;

namespace MonoDevelop.Ide.Editor
{
	/// <summary>
	/// The TextEditor object needs to be available through BaseViewContent.GetContent therefore we need to insert a 
	/// decorator in between.
	/// </summary>
	class TextEditorViewContent : ViewContent, ICommandRouter, IQuickTaskProvider
	{
		readonly TextEditor textEditor;
		readonly ITextEditorImpl textEditorImpl;

		DocumentContext currentContext;
		MonoDevelop.Projects.Policies.PolicyContainer policyContainer;

		public TextEditorViewContent (TextEditor textEditor, ITextEditorImpl textEditorImpl)
		{
			if (textEditor == null)
				throw new ArgumentNullException (nameof (textEditor));
			if (textEditorImpl == null)
				throw new ArgumentNullException (nameof (textEditorImpl));
			this.textEditor = textEditor;
			this.textEditorImpl = textEditorImpl;
			this.textEditor.MimeTypeChanged += UpdateTextEditorOptions;
			DefaultSourceEditorOptions.Instance.Changed += UpdateTextEditorOptions;
			this.textEditor.DocumentContextChanged += HandleDocumentContextChanged;
		}

		void HandleDocumentContextChanged (object sender, EventArgs e)
		{
			if (currentContext != null)
				currentContext.DocumentParsed -= HandleDocumentParsed;
			currentContext = textEditor.DocumentContext;
			currentContext.DocumentParsed += HandleDocumentParsed;
		}

		void HandleDirtyChanged (object sender, EventArgs e)
		{
			IsDirty = textEditorImpl.ViewContent.IsDirty;
			InformAutoSave ();
		}

		void HandleTextChanged (object sender, MonoDevelop.Core.Text.TextChangeEventArgs e)
		{
			InformAutoSave ();
		}

		void UpdateTextEditorOptions (object sender, EventArgs e)
		{
			UpdateStyleParent (Project, textEditor.MimeType);
		}

		uint autoSaveTimer = 0;
		Task autoSaveTask;
		void InformAutoSave ()
		{
			if (isDisposed)
				return;
			RemoveAutoSaveTimer ();
			autoSaveTimer = GLib.Timeout.Add (500, delegate {
				if (autoSaveTask != null && !autoSaveTask.IsCompleted)
					return false;

				autoSaveTask = AutoSave.InformAutoSaveThread (textEditor.CreateSnapshot (), textEditor.FileName, IsDirty);
				autoSaveTimer = 0;
				return false;
			});
		}


		void RemoveAutoSaveTimer ()
		{
			if (autoSaveTimer == 0)
				return;
			GLib.Source.Remove (autoSaveTimer);
			autoSaveTimer = 0;
		}

		void RemovePolicyChangeHandler ()
		{
			if (policyContainer != null)
				policyContainer.PolicyChanged -= HandlePolicyChanged;
		}

		void UpdateStyleParent (MonoDevelop.Projects.Project styleParent, string mimeType)
		{
			RemovePolicyChangeHandler ();

			if (string.IsNullOrEmpty (mimeType))
				mimeType = "text/plain";

			var mimeTypes = DesktopService.GetMimeTypeInheritanceChain (mimeType);

			if (styleParent != null)
				policyContainer = styleParent.Policies;
			else
				policyContainer = MonoDevelop.Projects.Policies.PolicyService.DefaultPolicies;
			var currentPolicy = policyContainer.Get<TextStylePolicy> (mimeTypes);

			policyContainer.PolicyChanged += HandlePolicyChanged;
			textEditor.Options = DefaultSourceEditorOptions.Instance.WithTextStyle (currentPolicy);
		}

		void HandlePolicyChanged (object sender, MonoDevelop.Projects.Policies.PolicyChangedEventArgs args)
		{
			var mimeTypes = DesktopService.GetMimeTypeInheritanceChain (textEditor.MimeType);
			var currentPolicy = policyContainer.Get<TextStylePolicy> (mimeTypes);
			textEditor.Options = DefaultSourceEditorOptions.Instance.WithTextStyle (currentPolicy);
		}

		void HandleDocumentParsed (object sender, EventArgs e)
		{
			var ctx = (DocumentContext)sender;
			CancelDocumentParsedUpdate ();
			var token = src.Token;
			var caretLocation = textEditor.CaretLocation;
			Task.Run (async () => {
				try {
					await UpdateErrorUndelines (ctx, ctx.ParsedDocument, token);
					await UpdateQuickTasks (ctx, ctx.ParsedDocument, token);
					await UpdateFoldings (ctx.ParsedDocument, caretLocation, false, token);
				} catch (OperationCanceledException) {
					// ignore
				}
			}, token);
		}

		void CancelDocumentParsedUpdate ()
		{
			src.Cancel ();
			src = new CancellationTokenSource ();
		}

		#region Error handling
		List<IErrorMarker> errors = new List<IErrorMarker> ();
		uint resetTimerId;

		void RemoveErrorUndelinesResetTimerId ()
		{
			if (resetTimerId > 0) {
				GLib.Source.Remove (resetTimerId);
				resetTimerId = 0;
			}
		}

		void RemoveErrorUnderlines ()
		{
			errors.ForEach (err => textEditor.RemoveMarker (err));
			errors.Clear ();
		}

		void UnderLineError (MonoDevelop.Ide.TypeSystem.Error info)
		{
			var error = TextMarkerFactory.CreateErrorMarker (textEditor, info);
			textEditor.AddMarker (error); 
			errors.Add (error);
		}

		static string [] lexicalError = {
			"CS0594", // ERR_FloatOverflow
			"CS0595", // ERR_InvalidReal
			"CS1009", // ERR_IllegalEscape
			"CS1010", // ERR_NewlineInConst
			"CS1011", // ERR_EmptyCharConst
			"CS1012", // ERR_TooManyCharsInConst
			"CS1015", // ERR_TypeExpected
			"CS1021", // ERR_IntOverflow
			"CS1032", // ERR_PPDefFollowsTokenpp
			"CS1035", // ERR_OpenEndedComment
			"CS1039", // ERR_UnterminatedStringLit
			"CS1040", // ERR_BadDirectivePlacementpp
			"CS1056", // ERR_UnexpectedCharacter
			"CS1056", // ERR_UnexpectedCharacter_EscapedBackslash
			"CS1646", // ERR_ExpectedVerbatimLiteral
			"CS0078", // WRN_LowercaseEllSuffix
			"CS1002", // ; expected
			"CS1519", // Invalid token ';' in class, struct, or interface member declaration
			"CS1031", // Type expected
			"CS0106", // The modifier 'readonly' is not valid for this item
			"CS1576", // The line number specified for #line directive is missing or invalid
			"CS1513" // } expected
		};

		async Task UpdateErrorUndelines (DocumentContext ctx, ParsedDocument parsedDocument, CancellationToken token)
		{
			if (!DefaultSourceEditorOptions.Instance.UnderlineErrors || parsedDocument == null || isDisposed)
				return;
			try {
				var errors = await parsedDocument.GetErrorsAsync(token).ConfigureAwait (false);
				Application.Invoke (delegate {
					if (token.IsCancellationRequested || isDisposed)
						return;
					RemoveErrorUndelinesResetTimerId ();
					const uint timeout = 500;
					resetTimerId = GLib.Timeout.Add (timeout, delegate {
						if (token.IsCancellationRequested) {
							resetTimerId = 0;
							return false;
						}
						RemoveErrorUnderlines ();
						// Else we underline the error
						if (errors != null) {
							foreach (var error in errors) {
								if (ctx.IsAdHocProject && !lexicalError.Contains (error.Id))
									continue;
								UnderLineError (error);
							}
						}
						resetTimerId = 0;
						return false;
					});
				});
			} catch (OperationCanceledException) {
				// ignore
			}
		}
		#endregion
		CancellationTokenSource src = new CancellationTokenSource ();
		async Task UpdateFoldings (ParsedDocument parsedDocument, DocumentLocation caretLocation, bool firstTime = false, CancellationToken token = default (CancellationToken))
		{
			if (parsedDocument == null || !textEditor.Options.ShowFoldMargin || isDisposed)
				return;
			// don't update parsed documents that contain errors - the foldings from there may be invalid.
			if (parsedDocument.HasErrors)
				return;
			
			try {
				var foldSegments = new List<IFoldSegment> ();

				foreach (FoldingRegion region in await parsedDocument.GetFoldingsAsync(token)) {
					if (token.IsCancellationRequested)
						return;
					var type = FoldingType.Unknown;
					bool setFolded = false;
					bool folded = false;
					//decide whether the regions should be folded by default
					switch (region.Type) {
					case FoldType.Member:
						type = FoldingType.TypeMember;
						break;
					case FoldType.Type:
						type = FoldingType.TypeDefinition;
						break;
					case FoldType.UserRegion:
						type = FoldingType.Region;
						setFolded = DefaultSourceEditorOptions.Instance.DefaultRegionsFolding;
						folded = true;
						break;
					case FoldType.Comment:
						type = FoldingType.Comment;
						setFolded = DefaultSourceEditorOptions.Instance.DefaultCommentFolding;
						folded = true;
						break;
					case FoldType.CommentInsideMember:
						type = FoldingType.Comment;
						setFolded = DefaultSourceEditorOptions.Instance.DefaultCommentFolding;
						folded = false;
						break;
					case FoldType.Undefined:
						setFolded = true;
						folded = region.IsFoldedByDefault;
						break;
					}
					var start = textEditor.LocationToOffset (region.Region.Begin);
					var end = textEditor.LocationToOffset (region.Region.End);
					var marker = textEditor.CreateFoldSegment (start, end - start);
					foldSegments.Add (marker);
					marker.CollapsedText = region.Name;
					marker.FoldingType = type;
					//and, if necessary, set its fold state
					if (marker != null && setFolded && firstTime) {
						// only fold on document open, later added folds are NOT folded by default.
						marker.IsCollapsed = folded;
						continue;
					}
					if (marker != null && region.Region.Contains (caretLocation.Line, caretLocation.Column))
						marker.IsCollapsed = false;
				}
				if (firstTime) {
					textEditor.SetFoldings (foldSegments);
				} else {
					Application.Invoke (delegate {
						if (!token.IsCancellationRequested)
							textEditor.SetFoldings (foldSegments);
					});
				}
			} catch (Exception ex) {
				LoggingService.LogError ("Unhandled exception in ParseInformationUpdaterWorkerThread", ex);
			}
		}

		async Task RunFirstTimeFoldUpdate (string text)
		{
			if (string.IsNullOrEmpty (text)) 
				return;
			ParsedDocument parsedDocument = null;

			var foldingParser = TypeSystemService.GetFoldingParser (textEditor.MimeType);
			if (foldingParser != null) {
				parsedDocument = foldingParser.Parse (textEditor.FileName, text);
			} else {
				var normalParser = TypeSystemService.GetParser (textEditor.MimeType);
				if (normalParser != null) {
					parsedDocument = await normalParser.Parse(
						new MonoDevelop.Ide.TypeSystem.ParseOptions {
							FileName = textEditor.FileName,
							Content = new StringTextSource(text),
							Project = Project
						});
				}
			}
			if (parsedDocument != null) {
				await UpdateFoldings (parsedDocument, textEditor.CaretLocation, true);
			}
		}

		protected override void OnContentNameChanged ()
		{
			base.OnContentNameChanged ();
			textEditorImpl.ContentName = ContentName;
		}


		#region IViewFContent implementation

		public override async Task Load (FileOpenInformation fileOpenInformation)
		{
			textEditorImpl.ViewContent.DirtyChanged -= HandleDirtyChanged;
			textEditor.TextChanged -= HandleTextChanged;
			await textEditorImpl.ViewContent.Load (fileOpenInformation);
			await RunFirstTimeFoldUpdate (textEditor.Text);
			textEditorImpl.InformLoadComplete ();
			textEditor.TextChanged += HandleTextChanged;
			textEditorImpl.ViewContent.DirtyChanged += HandleDirtyChanged;
		}

		public override async Task LoadNew (Stream content, string mimeType)
		{
			textEditor.MimeType = mimeType;
			string text = null;
			if (content != null) {
				var res = await TextFileUtility.GetTextAsync (content);
				text = textEditor.Text = res.Text;
				textEditor.Encoding = res.Encoding;
				textEditor.UseBOM = res.HasBom;
			}
			await RunFirstTimeFoldUpdate (text);
			textEditorImpl.InformLoadComplete ();
		}

		public override Task Save (FileSaveInformation fileSaveInformation)
		{
			if (!string.IsNullOrEmpty (fileSaveInformation.FileName))
				AutoSave.RemoveAutoSaveFile (fileSaveInformation.FileName);
			return textEditorImpl.ViewContent.Save (fileSaveInformation);
		}

		public override Task Save ()
		{
			if (!string.IsNullOrEmpty (textEditorImpl.ContentName))
				AutoSave.RemoveAutoSaveFile (textEditorImpl.ContentName);
			return textEditorImpl.ViewContent.Save ();
		}

		public override void DiscardChanges ()
		{
			if (autoSaveTask != null)
				autoSaveTask.Wait (TimeSpan.FromSeconds (5));
			RemoveAutoSaveTimer ();
			if (!string.IsNullOrEmpty (textEditorImpl.ContentName))
				AutoSave.RemoveAutoSaveFile (textEditorImpl.ContentName);
			textEditorImpl.ViewContent.DiscardChanges ();
		}

		protected override void OnSetProject (MonoDevelop.Projects.Project project)
		{
			base.OnSetProject (project);
			textEditorImpl.ViewContent.Project = project;
			UpdateTextEditorOptions (null, null);
		}

		public override ProjectReloadCapability ProjectReloadCapability {
			get {
				return textEditorImpl.ViewContent.ProjectReloadCapability;
			}
		}

		#endregion

		#region BaseViewContent implementation

		protected override IEnumerable<object> OnGetContents (Type type)
		{
			var res = base.OnGetContents (type);

			if (type == typeof (TextEditor))
				return res.Concat (textEditor);

			var ext = textEditorImpl.EditorExtension;
			while (ext != null) {
				res = res.Concat (ext.OnGetContents (type));
				ext = ext.Next;
			}
			res = res.Concat (textEditorImpl.ViewContent.GetContents (type));
			return res;
		}

		protected override void OnWorkbenchWindowChanged ()
		{
			base.OnWorkbenchWindowChanged ();
			textEditorImpl.ViewContent.WorkbenchWindow = WorkbenchWindow;
		}

		public override Control Control {
			get {
				return textEditor;
			}
		}

		public override string TabPageLabel {
			get {
				return textEditorImpl.ViewContent.TabPageLabel;
			}
		}

		#endregion

		#region IDisposable implementation
		bool isDisposed;

		public override void Dispose ()
		{
			if (isDisposed)
				return;
			
			base.Dispose ();

			isDisposed = true;
			CancelDocumentParsedUpdate ();
			textEditorImpl.ViewContent.DirtyChanged -= HandleDirtyChanged;
			textEditor.MimeTypeChanged -= UpdateTextEditorOptions;
			textEditor.TextChanged -= HandleTextChanged;
			textEditor.DocumentContextChanged -= HandleDocumentContextChanged;

			currentContext.DocumentParsed -= HandleDocumentParsed;
			DefaultSourceEditorOptions.Instance.Changed -= UpdateTextEditorOptions;
			RemovePolicyChangeHandler ();
			RemoveAutoSaveTimer ();
			RemoveErrorUndelinesResetTimerId ();
		}

		#endregion

		#region ICommandRouter implementation

		object ICommandRouter.GetNextCommandTarget ()
		{
			return textEditorImpl;
		}

		#endregion
	
		#region Commands
		void ToggleCodeCommentWithBlockComments ()
		{
			var blockStarts = TextEditorFactory.GetSyntaxProperties (textEditor.MimeType, "BlockCommentStart");
			var blockEnds = TextEditorFactory.GetSyntaxProperties (textEditor.MimeType, "BlockCommentEnd");
			if (blockStarts == null || blockEnds == null || blockStarts.Length == 0 || blockEnds.Length == 0)
				return;

			string blockStart = blockStarts[0];
			string blockEnd = blockEnds[0];

			using (var undo = textEditor.OpenUndoGroup ()) {
				IDocumentLine startLine;
				IDocumentLine endLine;

				if (textEditor.IsSomethingSelected) {
					startLine = textEditor.GetLineByOffset (textEditor.SelectionRange.Offset);
					endLine = textEditor.GetLineByOffset (textEditor.SelectionRange.EndOffset);

					// If selection ends at begining of line... This is visible as previous line
					// is selected, hence we want to select previous line Bug 26287
					if (endLine.Offset == textEditor.SelectionRange.EndOffset)
						endLine = endLine.PreviousLine;
				} else {
					startLine = endLine = textEditor.GetLine (textEditor.CaretLine);
				}
				string startLineText = textEditor.GetTextAt (startLine.Offset, startLine.Length);
				string endLineText = textEditor.GetTextAt (endLine.Offset, endLine.Length);
				if (startLineText.StartsWith (blockStart, StringComparison.Ordinal) && endLineText.EndsWith (blockEnd, StringComparison.Ordinal)) {
					textEditor.RemoveText (endLine.Offset + endLine.Length - blockEnd.Length, blockEnd.Length);
					textEditor.RemoveText (startLine.Offset, blockStart.Length);
					if (textEditor.IsSomethingSelected) {
						textEditor.SelectionAnchorOffset -= blockEnd.Length;
					}
				} else {
					textEditor.InsertText (endLine.Offset + endLine.Length, blockEnd);
					textEditor.InsertText (startLine.Offset, blockStart);
					if (textEditor.IsSomethingSelected) {
						textEditor.SelectionAnchorOffset += blockEnd.Length;
					}
				}
			}
		}

		bool TryGetLineCommentTag (out string commentTag)
		{
			var lineComments = TextEditorFactory.GetSyntaxProperties (textEditor.MimeType, "LineComment");
			if (lineComments == null || lineComments.Length == 0) {
				commentTag = null;
				return false;
			}
			commentTag = lineComments [0];
			return true;
		}

		[CommandUpdateHandler (EditCommands.AddCodeComment)]
		[CommandUpdateHandler (EditCommands.RemoveCodeComment)]
		[CommandUpdateHandler (EditCommands.ToggleCodeComment)]
		void OnUpdateToggleComment (CommandInfo info)
		{
			var lineComments = TextEditorFactory.GetSyntaxProperties (textEditor.MimeType, "LineComment");
			if (lineComments != null && lineComments.Length > 0) {
				info.Visible = true;
				return;
			}
			var blockStarts = TextEditorFactory.GetSyntaxProperties (textEditor.MimeType, "BlockCommentStart");
			var blockEnds = TextEditorFactory.GetSyntaxProperties (textEditor.MimeType, "BlockCommentEnd");
			info.Visible = blockStarts != null && blockStarts.Length > 0 && blockEnds != null && blockEnds.Length > 0;
		}

		[CommandHandler (EditCommands.ToggleCodeComment)]
		internal void ToggleCodeComment ()
		{
			string commentTag;
			if (!TryGetLineCommentTag (out commentTag))
				return;
			bool comment = false;
			foreach (var line in GetSelectedLines (textEditor)) {
				int startOffset;
				int offset = line.Offset;
				if (!StartsWith (textEditor, offset, line.Length, commentTag, out startOffset)) {
					if (startOffset - offset == line.Length) // case: line consists only of white spaces
						continue;
					comment = true;
					break;
				}
			}

			if (comment) {
				AddCodeComment ();
			} else {
				RemoveCodeComment ();
			}
		}

		static bool StartsWith (ITextSource text, int offset, int length, string commentTag, out int startOffset)
		{
			int max = Math.Min (offset + length, text.Length);
			int i = offset;
			for (; i < max; i++) {
				char ch = text.GetCharAt (i);
				if (ch != ' ' && ch != '\t')
					break;
			}
			startOffset = i;
			for (int j = 0; j < commentTag.Length && i < text.Length; j++) {
				if (text.GetCharAt (i) != commentTag [j])
					return false;
				i++;
			}

			return true;
		}

		static IEnumerable<IDocumentLine> GetSelectedLines (TextEditor textEditor)
		{
			if (!textEditor.IsSomethingSelected) {
				yield return textEditor.GetLine (textEditor.CaretLine);
				yield break;
            }
			var selection = textEditor.SelectionRegion;
			var line = textEditor.GetLine(selection.EndLine);
			if (selection.EndColumn == 1)
				line = line.PreviousLine;
			
			while (line != null && line.LineNumber >= selection.BeginLine) {
				yield return line;
				line = line.PreviousLine;
			}
		}

		[CommandHandler (EditCommands.AddCodeComment)]
		internal void AddCodeComment ()
		{
			string commentTag;
			if (!TryGetLineCommentTag (out commentTag))
				return;

			using (var undo = textEditor.OpenUndoGroup ()) {
				var wasSelected = textEditor.IsSomethingSelected;
				var lead = textEditor.SelectionLeadOffset;
				var anchor = textEditor.SelectionAnchorOffset;
				var lineAndIndents = new List<Tuple<IDocumentLine, string>>();
				string indent = null;
				var oldVersion = textEditor.Version;
				foreach (var line in GetSelectedLines (textEditor)) {
					var curIndent = line.GetIndentation (textEditor);
					if (line.Length == curIndent.Length) {
						lineAndIndents.Add (Tuple.Create ((IDocumentLine)null, ""));
						continue;
					}
					if (indent == null || curIndent.Length < indent.Length)
						indent = curIndent;
					lineAndIndents.Add (Tuple.Create (line, curIndent));
				}

				foreach (var line in lineAndIndents) {
					if (line.Item1 == null)
						continue;
					textEditor.InsertText (line.Item1.Offset + indent.Length, commentTag);
				}
				if (wasSelected) {
					textEditor.SelectionAnchorOffset = oldVersion.MoveOffsetTo (textEditor.Version, anchor);
					textEditor.SelectionLeadOffset = oldVersion.MoveOffsetTo (textEditor.Version, lead);
				}
			}
		}

		[CommandHandler (EditCommands.RemoveCodeComment)]
		internal void RemoveCodeComment ()
		{
			string commentTag;
			if (!TryGetLineCommentTag (out commentTag))
				return;
			
			using (var undo = textEditor.OpenUndoGroup ()) {
				var wasSelected = textEditor.IsSomethingSelected;
				var lead = textEditor.SelectionLeadOffset;
				var anchor = textEditor.SelectionAnchorOffset;
				int lines = 0;
				
				//IDocumentLine first = null;
				IDocumentLine last  = null;
				var oldVersion = textEditor.Version;
				foreach (var line in GetSelectedLines (textEditor)) {
					int startOffset;
					if (StartsWith (textEditor, line.Offset, line.Length, commentTag, out startOffset)) {
						textEditor.RemoveText (startOffset, commentTag.Length);
						lines++;
					}
					
					//first = line;
					if (last == null)
						last = line;
				}

				if (wasSelected) {
//					if (IdeApp.Workbench != null)
//						CodeFormatterService.Format (textEditor, IdeApp.Workbench.ActiveDocument, TextSegment.FromBounds (first.Offset, last.EndOffset));

					textEditor.SelectionAnchorOffset = oldVersion.MoveOffsetTo (textEditor.Version, anchor);
					textEditor.SelectionLeadOffset = oldVersion.MoveOffsetTo (textEditor.Version, lead);
				}
			}
		}

		[CommandHandler (EditCommands.InsertGuid)]
		void InsertGuid ()
		{
			textEditor.InsertAtCaret (Guid.NewGuid ().ToString ());
		}

		[CommandUpdateHandler (MessageBubbleCommands.Toggle)]
		public void OnUpdateToggleErrorTextMarker (CommandInfo info)
		{
			var line = textEditor.GetLine (textEditor.CaretLine);
			if (line == null) {
				info.Visible = false;
				return;
			}

			var marker = (IMessageBubbleLineMarker)textEditor.GetLineMarkers (line).FirstOrDefault (m => m is IMessageBubbleLineMarker);
			info.Visible = marker != null;
		}

		[CommandHandler (MessageBubbleCommands.Toggle)]
		public void OnToggleErrorTextMarker ()
		{
			var line = textEditor.GetLine (textEditor.CaretLine);
			if (line == null)
				return;
			var marker = (IMessageBubbleLineMarker)textEditor.GetLineMarkers (line).FirstOrDefault (m => m is IMessageBubbleLineMarker);
			if (marker != null) {
				marker.IsVisible = !marker.IsVisible;
			}
		}
		#endregion
	
		#region IQuickTaskProvider implementation
		ImmutableArray<QuickTask> tasks = ImmutableArray<QuickTask>.Empty;

		public event EventHandler TasksUpdated;

		protected virtual void OnTasksUpdated (EventArgs e)
		{
			EventHandler handler = this.TasksUpdated;
			if (handler != null)
				handler (this, e);
		}

		public ImmutableArray<QuickTask> QuickTasks {
			get {
				return tasks;
			}
		}

		async Task UpdateQuickTasks (DocumentContext ctx, ParsedDocument doc, CancellationToken token)
		{
			if (isDisposed)
				return;
			var newTasks = ImmutableArray<QuickTask>.Empty.ToBuilder ();
			if (doc != null) {
				foreach (var cmt in await doc.GetTagCommentsAsync(token).ConfigureAwait (false)) {
					if (token.IsCancellationRequested)
						return;
					int offset;
					try {
						offset = textEditor.LocationToOffset (cmt.Region.Begin.Line, cmt.Region.Begin.Column);
					} catch (Exception) {
						return;
					}
					var newTask = new QuickTask (cmt.Text, offset, DiagnosticSeverity.Info);
					newTasks.Add (newTask);
				}

				foreach (var error in await doc.GetErrorsAsync(token).ConfigureAwait (false)) {
					if (token.IsCancellationRequested)
						return;
					if (ctx.IsAdHocProject && !lexicalError.Contains (error.Id))
						continue;
					int offset;
					try {
						offset = textEditor.LocationToOffset (error.Region.Begin.Line, error.Region.Begin.Column);
					} catch (Exception) {
						return;
					}
					var newTask = new QuickTask (error.Message, offset, error.ErrorType == MonoDevelop.Ide.TypeSystem.ErrorType.Error ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning);
					newTasks.Add (newTask);
				}
			}
			if (token.IsCancellationRequested)
				return;
			Application.Invoke (delegate {
				if (token.IsCancellationRequested || isDisposed)
					return;
				tasks = newTasks.ToImmutable ();
				OnTasksUpdated (EventArgs.Empty);
			});
		}
		#endregion

		#region Key bindings

		[CommandHandler (TextEditorCommands.LineEnd)]
		void OnLineEnd ()
		{
			EditActions.MoveCaretToLineEnd (textEditor);
		}

		[CommandHandler (TextEditorCommands.LineStart)]
		void OnLineStart ()
		{
			EditActions.MoveCaretToLineStart (textEditor);
		}

		[CommandHandler (TextEditorCommands.DeleteLeftChar)]
		void OnDeleteLeftChar ()
		{
			EditActions.Backspace (textEditor);
		}

		[CommandHandler (TextEditorCommands.DeleteRightChar)]
		void OnDeleteRightChar ()
		{
			EditActions.Delete (textEditor);
		}

		[CommandHandler (TextEditorCommands.CharLeft)]
		void OnCharLeft ()
		{
			EditActions.MoveCaretLeft (textEditor);
		}

		[CommandHandler (TextEditorCommands.CharRight)]
		void OnCharRight ()
		{
			EditActions.MoveCaretRight (textEditor);
		}

		[CommandHandler (TextEditorCommands.LineUp)]
		void OnLineUp ()
		{
			EditActions.MoveCaretUp (textEditor);
		}

		[CommandHandler (TextEditorCommands.LineDown)]
		void OnLineDown ()
		{
			EditActions.MoveCaretDown (textEditor);
		}

		[CommandHandler (TextEditorCommands.DocumentStart)]
		void OnDocumentStart ()
		{
			EditActions.MoveCaretToDocumentStart (textEditor);
		}

		[CommandHandler (TextEditorCommands.DocumentEnd)]
		void OnDocumentEnd ()
		{
			EditActions.MoveCaretToDocumentEnd (textEditor);
		}

		[CommandHandler (TextEditorCommands.PageUp)]
		void OnPageUp ()
		{
			EditActions.PageUp (textEditor);
		}

		[CommandHandler (TextEditorCommands.PageDown)]
		void OnPageDown ()
		{
			EditActions.PageDown (textEditor);
		}

		[CommandHandler (TextEditorCommands.DeleteLine)]
		void OnDeleteLine ()
		{
			EditActions.DeleteCurrentLine (textEditor);
		}

		[CommandHandler (TextEditorCommands.DeleteToLineEnd)]
		void OnDeleteToLineEnd ()
		{
			EditActions.DeleteCurrentLineToEnd (textEditor);
		}

		[CommandHandler (TextEditorCommands.ScrollLineUp)]
		void OnScrollLineUp ()
		{
			EditActions.ScrollLineUp (textEditor);
		}

		[CommandHandler (TextEditorCommands.ScrollLineDown)]
		void OnScrollLineDown ()
		{
			EditActions.ScrollLineDown (textEditor);
		}

		[CommandHandler (TextEditorCommands.ScrollPageUp)]
		void OnScrollPageUp ()
		{
			EditActions.ScrollPageUp (textEditor);
		}

		[CommandHandler (TextEditorCommands.ScrollPageDown)]
		void OnScrollPageDown ()
		{
			EditActions.ScrollPageDown (textEditor);
		}

		[CommandHandler (TextEditorCommands.GotoMatchingBrace)]
		void OnGotoMatchingBrace ()
		{
			EditActions.GotoMatchingBrace (textEditor);
		}

		[CommandHandler (TextEditorCommands.SelectionMoveLeft)]
		void OnSelectionMoveLeft ()
		{
			EditActions.SelectionMoveLeft (textEditor);
		}

		[CommandHandler (TextEditorCommands.SelectionMoveRight)]
		void OnSelectionMoveRight ()
		{
			EditActions.SelectionMoveRight (textEditor);
		}

		[CommandHandler (TextEditorCommands.MovePrevWord)]
		void OnMovePrevWord ()
		{
			EditActions.MovePrevWord (textEditor);
		}

		[CommandHandler (TextEditorCommands.MoveNextWord)]
		void OnMoveNextWord ()
		{
			EditActions.MoveNextWord (textEditor);
		}

		[CommandHandler (TextEditorCommands.SelectionMovePrevWord)]
		void OnSelectionMovePrevWord ()
		{
			EditActions.SelectionMovePrevWord (textEditor);
		}

		[CommandHandler (TextEditorCommands.SelectionMoveNextWord)]
		void OnSelectionMoveNextWord ()
		{
			EditActions.SelectionMoveNextWord (textEditor);
		}

		[CommandHandler (TextEditorCommands.MovePrevSubword)]
		void OnMovePrevSubword ()
		{
			EditActions.MovePrevSubWord (textEditor);
		}

		[CommandHandler (TextEditorCommands.MoveNextSubword)]
		void OnMoveNextSubword ()
		{
			EditActions.MoveNextSubWord (textEditor);
		}

		[CommandHandler (TextEditorCommands.SelectionMovePrevSubword)]
		void OnSelectionMovePrevSubword ()
		{
			EditActions.SelectionMovePrevSubWord (textEditor);
		}

		[CommandHandler (TextEditorCommands.SelectionMoveNextSubword)]
		void OnSelectionMoveNextSubword ()
		{
			EditActions.SelectionMoveNextSubWord (textEditor);
		}

		[CommandHandler (TextEditorCommands.SelectionMoveUp)]
		void OnSelectionMoveUp ()
		{
			EditActions.SelectionMoveUp (textEditor);
		}

		[CommandHandler (TextEditorCommands.SelectionMoveDown)]
		void OnSelectionMoveDown ()
		{
			EditActions.SelectionMoveDown (textEditor);
		}

		[CommandHandler (TextEditorCommands.SelectionMoveHome)]
		void OnSelectionMoveHome ()
		{
			EditActions.SelectionMoveLineStart (textEditor);
		}

		[CommandHandler (TextEditorCommands.SelectionMoveEnd)]
		void OnSelectionMoveEnd ()
		{
			EditActions.SelectionMoveLineEnd (textEditor);
		}

		[CommandHandler (TextEditorCommands.SelectionMoveToDocumentStart)]
		void OnSelectionMoveToDocumentStart ()
		{
			EditActions.SelectionMoveToDocumentStart (textEditor);
		}

		[CommandHandler (TextEditorCommands.ExpandSelectionToLine)]
		void OnExpandSelectionToLine ()
		{
			EditActions.ExpandSelectionToLine (textEditor);
		}

		[CommandHandler (TextEditorCommands.SelectionMoveToDocumentEnd)]
		void OnSelectionMoveToDocumentEnd ()
		{
			EditActions.SelectionMoveToDocumentEnd (textEditor);
		}

		[CommandHandler (TextEditorCommands.SwitchCaretMode)]
		void OnSwitchCaretMode ()
		{
			EditActions.SwitchCaretMode (textEditor);
		}

		[CommandHandler (TextEditorCommands.InsertTab)]
		void OnInsertTab ()
		{
			EditActions.InsertTab (textEditor);
		}

		[CommandHandler (TextEditorCommands.RemoveTab)]
		void OnRemoveTab ()
		{
			EditActions.RemoveTab (textEditor);
		}

		[CommandHandler (TextEditorCommands.InsertNewLine)]
		void OnInsertNewLine ()
		{
			EditActions.InsertNewLine (textEditor);
		}

		[CommandHandler (TextEditorCommands.InsertNewLineAtEnd)]
		void OnInsertNewLineAtEnd ()
		{
			EditActions.InsertNewLineAtEnd (textEditor);
		}

		[CommandHandler (TextEditorCommands.InsertNewLinePreserveCaretPosition)]
		void OnInsertNewLinePreserveCaretPosition ()
		{
			EditActions.InsertNewLinePreserveCaretPosition (textEditor);
		}

		[CommandHandler (TextEditorCommands.CompleteStatement)]
		void OnCompleteStatement ()
		{
			var doc = IdeApp.Workbench.ActiveDocument;
			var generator = CodeGenerator.CreateGenerator (doc);
			if (generator != null) {
				generator.CompleteStatement (doc);
			}
		}

		[CommandHandler (TextEditorCommands.DeletePrevWord)]
		void OnDeletePrevWord ()
		{
			EditActions.DeletePreviousWord (textEditor);
		}

		[CommandHandler (TextEditorCommands.DeleteNextWord)]
		void OnDeleteNextWord ()
		{
			EditActions.DeleteNextWord (textEditor);
		}

		[CommandHandler (TextEditorCommands.DeletePrevSubword)]
		void OnDeletePrevSubword ()
		{
			EditActions.DeletePreviousSubword (textEditor);
		}

		[CommandHandler (TextEditorCommands.DeleteNextSubword)]
		void OnDeleteNextSubword ()
		{
			EditActions.DeleteNextSubword (textEditor);
		}

		[CommandHandler (TextEditorCommands.SelectionPageDownAction)]
		void OnSelectionPageDownAction ()
		{
			EditActions.SelectionPageDown (textEditor);
		}

		[CommandHandler (TextEditorCommands.SelectionPageUpAction)]
		void OnSelectionPageUpAction ()
		{
			EditActions.SelectionPageUp (textEditor);
		}

		[CommandHandler (TextEditorCommands.PulseCaret)]
		void OnPulseCaretCommand ()
		{
			EditActions.StartCaretPulseAnimation (textEditor);
		}

		[CommandHandler (TextEditorCommands.TransposeCharacters)]
		void TransposeCharacters ()
		{
			EditActions.TransposeCharacters (textEditor);
		}

		[CommandHandler (TextEditorCommands.DuplicateLine)]
		void DuplicateLine ()
		{	
			EditActions.DuplicateCurrentLine (textEditor);
		}

		[CommandHandler (TextEditorCommands.RecenterEditor)]
		void RecenterEditor ()
		{
			EditActions.RecenterEditor (textEditor);
		}

		[CommandHandler (EditCommands.JoinWithNextLine)]
		void JoinLines ()
		{
			EditActions.JoinLines (textEditor);
		}

		[CommandHandler (TextEditorCommands.MoveBlockUp)]
		void OnMoveBlockUp ()
		{
			EditActions.MoveBlockUp (textEditor);
		}

		[CommandHandler (TextEditorCommands.MoveBlockDown)]
		void OnMoveBlockDown ()
		{
			EditActions.MoveBlockDown (textEditor);
		}

		[CommandHandler (TextEditorCommands.ToggleBlockSelectionMode)]
		void OnToggleBlockSelectionMode ()
		{
			EditActions.ToggleBlockSelectionMode (textEditor);
		}

		[CommandHandler (EditCommands.IndentSelection)]
		void IndentSelection ()
		{
			EditActions.IndentSelection (textEditor);
		}

		[CommandHandler (EditCommands.UnIndentSelection)]
		void UnIndentSelection ()
		{
			EditActions.UnIndentSelection (textEditor);
		}


		[CommandHandler (EditCommands.SortSelectedLines)]
		void SortSelectedLines ()
		{
			EditActions.SortSelectedLines (textEditor);
		}

		[CommandUpdateHandler (EditCommands.SortSelectedLines)]
		void UpdateSortSelectedLines (CommandInfo ci)
		{
			var region = textEditor.SelectionRegion;
			ci.Enabled = region.BeginLine != region.EndLine;
		}
		#endregion

	}
}