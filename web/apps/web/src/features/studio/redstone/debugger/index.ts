/**
 * Where the redstone panel mounts the debugger: its three tabs and its keys. Everything in this folder reads the
 * simulation through `SimControllerApi` only.
 */
export { DebuggerPanel, type DebuggerPanelProps } from './DebuggerPanel';
export { bindDebuggerKeys, type DebuggerKeyOptions } from './debuggerKeys';
