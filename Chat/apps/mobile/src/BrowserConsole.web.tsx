export default function BrowserConsole({ url }: { url: string }) {
  return (
    <iframe
      title="Remote browser session console"
      src={url}
      style={{ height: 540, width: "100%", border: 0, borderRadius: 12, background: "#FFF" }}
    />
  );
}
