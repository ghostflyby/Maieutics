using Maieutics.Agent;

namespace Maieutics.Commands;

/// <summary>Adds the product's output authoring contract to each conversation run without
/// changing the configured profile or the provider-neutral Agent runtime.</summary>
internal sealed class OutputRenderingProfileProvider(IAgentRunProfileProvider inner) : IAgentRunProfileProvider
{
    internal const string Instructions = """
        ## Output rendering

        Maieutics can render assistant replies using a VitePress-based Markdown pipeline.
        Use ordinary Markdown for prose. When useful, you may use tables, reference links,
        syntax-highlighted code fences, code groups, and VitePress containers: tip, info,
        warning, danger, details. Put a container's opening marker, body, and closing marker
        on separate lines, for example:

        ::: tip Optional title
        Helpful supplementary information.
        :::

        For useful interactive output, you may write Vue directly in the Markdown reply:
        a complete top-level <script setup> (optionally lang="ts"), HTML/Vue markup, and
        optional <style scoped>. For example:

        <script setup>
        import { ref } from 'vue'
        const count = ref(0)
        </script>

        <button @click="count++">Count: {{ count }}</button>

        Write intended live content directly in the reply, outside code fences. Put examples
        that should remain literal inside code fences or inline code. In live Vue markup,
        v-pre preserves literal interpolation text. Include readable prose with interactive output.

        While a reply streams, it is rendered as passive Markdown; Vue is compiled and hydrated
        only after successful completion of the whole reply. Provide complete declarations;
        do not rely on future replies supplying bindings. Setup runs for SSR and again for
        client hydration: use deterministic initial state and clean up owned effects/listeners.
        Only imports from 'vue' are supported. Full VitePress site features, external components,
        filesystem includes, $frontmatter bindings, async setup/Suspense and client-only components
        are unavailable. Use authorized tools for filesystem, network and external producers;
        component setup is for presentation, not tool execution. Saved notebook output reopens
        passively and does not rerun component code.

        Follow the user's requested output format; use these features only when they help.
        """;

    public async Task<IAgentRunProfileLease> AcquireAsync(CancellationToken cancellationToken = default)
    {
        var lease = await inner.AcquireAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var profile = lease.Profile;
            var prompt = profile.Options.SystemPrompt;
            var composed = string.IsNullOrWhiteSpace(prompt)
                ? Instructions
                : $"{prompt.TrimEnd()}\n\n{Instructions}";
            var adjusted = new AgentRunProfile(
                profile.ChatClient,
                profile.Options with { SystemPrompt = composed },
                profile.ModelIdentity,
                profile.Capabilities,
                profile.HostedCapabilities,
                profile.Tools,
                profile.HostedTools);
            return new Lease(lease, adjusted);
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class Lease(IAgentRunProfileLease inner, AgentRunProfile profile) : IAgentRunProfileLease
    {
        public AgentRunProfile Profile => profile;

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
