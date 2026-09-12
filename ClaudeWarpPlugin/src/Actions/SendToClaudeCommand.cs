namespace Loupedeck.ClaudeWarpPlugin
{
    using System;

    // "Send to Claude" - one action, dropped on as many home-page keys as you like, each configured
    // in Options+ with its own text. No file to find and no restart.
    //
    // This is the only GUI-configurable surface the SDK permits. ActionEditorCommand works because
    // a top-level action has an identity that survives being saved into a profile, which is exactly
    // what a dynamic folder's generated action names do not - hence config.json for the folder row
    // and this for the home page. The two are not redundant; the SDK simply forces different
    // mechanisms in the two places.
    public class SendToClaudeCommand : ActionEditorCommand
    {
        private const String TextName = "text";
        private const String SubmitName = "submit";
        private const String LabelName = "label";

        public SendToClaudeCommand()
            : base((DeviceType)DeviceTypeAliases.MxCreativeKeypad)
        {
            this.DisplayName = "Send to Claude";
            this.Description = "Types text into the focused Warp pane, optionally pressing Return";
            this.GroupName = "Claude";
            this.IsWidget = true;

            this.ActionEditor.AddControlEx(
                new ActionEditorTextbox(TextName, "Text", "Typed into the focused Warp pane. Leave empty and tick Return for a plain Enter, which answers a plan or a question."));

            this.ActionEditor.AddControlEx(
                new ActionEditorCheckbox(SubmitName, "Press Return", "Submit straight away. Leave off for commands you want to add to, such as /compact."));

            this.ActionEditor.AddControlEx(
                new ActionEditorTextbox(LabelName, "Key label", "What the key shows. Defaults to the text itself."));
        }

        protected override Boolean RunCommand(ActionEditorActionParameters actionParameters)
        {
            var text = actionParameters.GetString(TextName, "");
            var submit = actionParameters.GetBoolean(SubmitName, false);

            // Nothing to type and no Return: an unconfigured key, not an error worth a warning.
            if (text.Length == 0 && !submit)
            {
                return false;
            }

            // No focus juggling on purpose. WarpInput refuses unless Warp is already frontmost, so
            // a mistimed press cannot type a slash command into whatever you were actually using.
            if (!WarpInput.TypeText(text, submit))
            {
                return false;
            }

            PluginLog.Info($"typed \"{text}\"{(submit ? " + Return" : "")} into the focused Warp pane");
            return true;
        }

        protected override BitmapImage GetCommandImage(
            ActionEditorActionParameters actionParameters, Int32 imageWidth, Int32 imageHeight) =>
            TileRenderer.Command(Label(actionParameters), null, imageWidth, imageHeight);

        protected override String GetCommandDisplayName(ActionEditorActionParameters actionParameters) => "";

        private static String Label(ActionEditorActionParameters actionParameters)
        {
            var label = actionParameters.GetString(LabelName, "");
            if (label.Length > 0)
            {
                return label;
            }

            var text = actionParameters.GetString(TextName, "").Trim();
            return text.Length > 0 ? text : "send";
        }
    }
}
