﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using NUnit.Framework;
using ICSharpCode.NRefactory6.CSharp.Completion;

namespace ICSharpCode.NRefactory6.CSharp.CodeCompletion.Roslyn
{
	[TestFixture]
	public class ExternAliasCompletionProviderTests : CompletionTestBase
	{
//		internal override ICompletionProvider CreateCompletionProvider()
//		{
//			return new ExternAliasCompletionProvider();
//		}
//
//		[Fact, Trait(Traits.Feature, Traits.Features.Completion)]
//		public void NoAliases()
//		{
//			VerifyNoItemsExist(@"
//extern alias $$
//class C
//{
//}");
//		}
//
//		[Fact, Trait(Traits.Feature, Traits.Features.Completion)]
//		public void ExternAlias()
//		{
//			var markup = @"
//extern alias $$ ";
//			VerifyItemWithAliasedMetadataReferences(markup, "foo", "foo", 1, "C#", "C#", false);
//		}
//
//		[Fact, Trait(Traits.Feature, Traits.Features.Completion)]
//		public void NotAfterExternAlias()
//		{
//			var markup = @"
//extern alias foo $$ ";
//			VerifyItemWithAliasedMetadataReferences(markup, "foo", "foo", 0, "C#", "C#", false);
//		}
//
//		[Fact, Trait(Traits.Feature, Traits.Features.Completion)]
//		public void NotGlobal()
//		{
//			var markup = @"
//extern alias $$ ";
//			VerifyItemWithAliasedMetadataReferences(markup, "foo", "global", 0, "C#", "C#", false);
//		}
//
//		[Fact, Trait(Traits.Feature, Traits.Features.Completion)]
//		public void NotIfAlreadyUsed()
//		{
//			var markup = @"
//extern alias foo;
//extern alias $$";
//			VerifyItemWithAliasedMetadataReferences(markup, "foo", "foo", 0, "C#", "C#", false);
//		}
//
//		[WorkItem(1075278)]
//		[Fact, Trait(Traits.Feature, Traits.Features.Completion)]
//		public void NotInComment()
//		{
//			var markup = @"
//extern alias // $$ ";
//			VerifyNoItemsExist(markup);
//		}
	}
}
