import {
  createContext,
  type Dispatch,
  type ReactNode,
  type SetStateAction,
  useCallback,
  useContext,
  useState,
} from "react";

interface ComputerDrafts {
  tab: "Browser" | "Terminal" | "Files";
  command: string;
  cwd: string;
  path: string;
  editor: { path: string; text: string; saved: string; savedPath: string } | undefined;
}
const DraftContext = createContext<{
  drafts: ComputerDrafts;
  setDrafts: Dispatch<SetStateAction<ComputerDrafts>>;
} | null>(null);

/** Keep unsent work when another sheet replaces the computer; never persist it to disk. */
export function ComputerDraftProvider({ children }: { children: ReactNode }) {
  const [drafts, setDrafts] = useState<ComputerDrafts>({
    tab: "Browser",
    command: "",
    cwd: "/workspace",
    path: "/workspace",
    editor: undefined,
  });
  return <DraftContext.Provider value={{ drafts, setDrafts }}>{children}</DraftContext.Provider>;
}
export function useComputerDraft<K extends keyof ComputerDrafts>(key: K) {
  const context = useContext(DraftContext);
  if (!context) throw new Error("Computer drafts are unavailable");
  const { setDrafts } = context;
  const set = useCallback(
    (value: SetStateAction<ComputerDrafts[K]>) => {
      setDrafts((current) => ({
        ...current,
        [key]: typeof value === "function" ? value(current[key]) : value,
      }));
    },
    [key, setDrafts],
  );
  return [context.drafts[key], set] as const;
}
