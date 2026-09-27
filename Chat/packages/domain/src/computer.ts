export interface ComputerCommand {
  id: string;
  command: string;
  cwd: string;
  status: "running" | "succeeded" | "failed" | "timed_out" | "interrupted";
  exitCode?: number;
  stdout: string;
  stderr: string;
  truncated: boolean;
  startedAt: string;
  completedAt?: string;
}
export interface ComputerSnapshot {
  enabled: boolean;
  provider: "docker";
  status: "unconfigured" | "stopped" | "running" | "error";
  workspacePath: "/workspace";
  network: "disabled";
  message?: string;
  commands: ComputerCommand[];
}
export interface ComputerDirectory {
  path: string;
  entries: { name: string; path: string; type: "file" | "directory" | "symlink"; size: number }[];
}
