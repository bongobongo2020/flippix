using System;
using System.Collections.Generic;
using System.IO;
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using FlipPix.UI.ViewModels.Video;

namespace FlipPix.Tests;

/// <summary>
/// 🌀 MiniMax I2V's idea-box autosave: when typing keeps updating a draft and when it must leave the old
/// text behind, and that a draft written to disk is there for the next launch.
/// </summary>
public sealed class MiniMaxI2VDraftTests
{
    [Theory]
    [InlineData("A wom", "A woman walks")]
    [InlineData("A woman walks", "A woman")]
    [InlineData("A woman walks home", "A woman slowly walks home")]
    [InlineData("walks home", "A woman walks home")]
    public void Editing_stays_on_the_same_draft(string current, string updated) =>
        Assert.True(MiniMaxI2VViewModel.IsSameDraft(current, updated));

    [Theory]
    [InlineData("A woman walks home", "Ref2VA: subject_definitions: none")]
    [InlineData("A woman walks home", "")]
    [InlineData("", "A woman walks home")]
    public void Replacing_the_text_starts_a_new_draft(string current, string updated) =>
        Assert.False(MiniMaxI2VViewModel.IsSameDraft(current, updated));

    [Fact]
    public void A_saved_draft_survives_into_a_new_library_instance()
    {
        var folder = Path.Combine(Path.GetTempPath(), "flippix-drafts-" + Guid.NewGuid().ToString("N"));
        try
        {
            var draft = new ScenePrompt { Name = "Idea", Prompt = "A woman walks home" };
            new ScenePromptLibrary(null, folder).Save(new List<ScenePrompt> { draft });

            var loaded = Assert.Single(new ScenePromptLibrary(null, folder).Load());
            Assert.Equal(draft.Id, loaded.Id);
            Assert.Equal(draft.Prompt, loaded.Prompt);
            Assert.False(File.Exists(Path.Combine(folder, "index.json.tmp")));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
