import { ChevronLeft, ChevronRight, Minus, Plus } from "lucide-react-native";
import { useRef, useState } from "react";
import { Text, View } from "react-native";
import Pdf from "react-native-pdf";
import { Button, colors, ErrorNotice, s } from "./ui";
export interface PdfReaderProps {
  url: string;
  token: string;
  pageCount: number;
}
export default function PdfReader({ url, token, pageCount }: PdfReaderProps) {
  const ref = useRef<React.ComponentRef<typeof Pdf>>(null);
  const [page, setPage] = useState(1);
  const [scale, setScale] = useState(1);
  const [error, setError] = useState("");
  const [pages, setPages] = useState(pageCount);
  return (
    <View style={{ gap: 12 }}>
      <View style={[s.between, { gap: 8, flexWrap: "wrap" }]}>
        <View style={[s.row, { gap: 8 }]}>
          <Button
            small
            icon={ChevronLeft}
            disabled={page <= 1}
            onPress={() => ref.current?.setPage(page - 1)}
          >
            Previous
          </Button>
          <Text style={s.small}>
            {page} / {pages}
          </Text>
          <Button
            small
            icon={ChevronRight}
            disabled={page >= pages}
            onPress={() => ref.current?.setPage(page + 1)}
          >
            Next
          </Button>
        </View>
        <View style={[s.row, { gap: 8 }]}>
          <Button
            small
            icon={Minus}
            disabled={scale <= 1}
            onPress={() => setScale(Math.max(1, scale - 0.25))}
          >
            Zoom out
          </Button>
          <Button
            small
            icon={Plus}
            disabled={scale >= 3}
            onPress={() => setScale(Math.min(3, scale + 0.25))}
          >
            Zoom in
          </Button>
        </View>
      </View>
      <ErrorNotice error={error} />
      <Pdf
        ref={ref}
        source={{ uri: url, headers: { Authorization: `Bearer ${token}` }, cache: false }}
        trustAllCerts={false}
        scale={scale}
        onLoadComplete={(n) => setPages(n)}
        onPageChanged={(p) => setPage(p)}
        onError={(e) => setError(String(e))}
        style={{ height: 530, width: "100%", backgroundColor: colors.line, borderRadius: 12 }}
      />
    </View>
  );
}
