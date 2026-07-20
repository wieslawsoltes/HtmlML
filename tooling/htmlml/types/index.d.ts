export type HtmlMlCapability =
  | 'dom' | 'css.layout' | 'canvas.2d' | 'svg'
  | 'input.pointer' | 'input.keyboard' | 'input.focus' | 'clipboard'
  | 'host.commands' | 'host.settings' | 'host.notifications'
  | 'host.network' | 'host.clipboard' | 'host.files';

export interface HtmlMlInvokeOptions { signal?: AbortSignal; }

export interface HtmlMlCapabilityClient {
  invoke<TResult = unknown, TArguments = unknown>(method: string, argumentsValue?: TArguments, options?: HtmlMlInvokeOptions): Promise<TResult>;
}

export interface HtmlMlHost {
  readonly commands: HtmlMlCapabilityClient;
  readonly settings: HtmlMlCapabilityClient;
  readonly notifications: HtmlMlCapabilityClient;
  readonly network: HtmlMlCapabilityClient;
  readonly clipboard: HtmlMlCapabilityClient;
  readonly files: HtmlMlCapabilityClient;
}

export interface HtmlMlRuntime {
  readonly profileVersion: '1.0';
  readonly host: HtmlMlHost;
}

export declare const htmlml: HtmlMlRuntime;

declare global { const htmlml: HtmlMlRuntime; }
