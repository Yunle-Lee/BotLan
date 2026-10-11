import { ArrowUpRight, Check, ChevronRight, Copy, type LucideIcon, X } from "lucide-react-native";
import { type ReactNode, useEffect, useRef, useState } from "react";
import {
  AccessibilityInfo,
  ActivityIndicator,
  Animated,
  Image,
  Modal,
  Platform,
  Pressable,
  ScrollView,
  StyleSheet,
  Text,
  TextInput,
  type TextInputProps,
  useWindowDimensions,
  View,
  type ViewStyle,
} from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
export const colors = {
  canvas: "#FCFCFC",
  card: "#FFFFFF",
  text: "#11191C",
  muted: "#697176",
  line: "#EEEEF0",
  blue: "#C8E7FF",
  blueDark: "#1473C8",
  sky: "#EDF7FD",
  green: "#E3F3E8",
  lavender: "#F0EEFA",
  orange: "#FDF0DF",
  danger: "#AA4A45",
};
export const s = StyleSheet.create({
  row: { flexDirection: "row", alignItems: "center" },
  between: { flexDirection: "row", alignItems: "center", justifyContent: "space-between" },
  text: { color: colors.text, fontSize: 15, lineHeight: 23 },
  muted: { color: colors.muted, fontSize: 14, lineHeight: 21 },
  small: { color: colors.muted, fontSize: 11, lineHeight: 17 },
  label: {
    color: colors.muted,
    fontSize: 10,
    fontWeight: "700",
    letterSpacing: 1.4,
    textTransform: "uppercase",
  },
  title: { color: colors.text, fontSize: 23, fontWeight: "600", letterSpacing: -0.7 },
  heading: { color: colors.text, fontSize: 16, fontWeight: "600", letterSpacing: -0.25 },
  card: {
    backgroundColor: colors.card,
    borderRadius: 23,
    borderWidth: 0,
    borderColor: colors.line,
    padding: 20,
  },
  divider: { height: 1, backgroundColor: colors.line, marginVertical: 18 },
  input: {
    borderWidth: 1,
    borderColor: colors.line,
    borderRadius: 19,
    paddingHorizontal: 16,
    paddingVertical: 12,
    color: colors.text,
    fontSize: 16,
    backgroundColor: "#FFF",
    minHeight: 45,
  },
  field: { gap: 7, marginBottom: 16 },
  button: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "center",
    gap: 8,
    paddingHorizontal: 17,
    minHeight: 42,
    paddingVertical: 10,
    borderRadius: 24,
  },
  primary: { backgroundColor: colors.blue },
  secondary: { backgroundColor: "#F1F2F3" },
  buttonText: { fontSize: 14, fontWeight: "600" },
  chip: {
    paddingHorizontal: 10,
    paddingVertical: 4,
    borderRadius: 20,
    alignSelf: "flex-start",
    backgroundColor: colors.canvas,
  },
  chipText: { fontSize: 10, fontWeight: "600", color: colors.muted },
  iconBox: {
    width: 42,
    height: 42,
    borderRadius: 13,
    justifyContent: "center",
    alignItems: "center",
    backgroundColor: colors.sky,
  },
  error: { padding: 16, borderRadius: 14, backgroundColor: "#FBEFED", marginVertical: 10, gap: 4 },
  modalShade: {
    flex: 1,
    backgroundColor: "rgba(35,48,44,0.25)",
    justifyContent: "center",
    alignItems: "center",
    padding: 20,
  },
  sheet: {
    backgroundColor: colors.canvas,
    borderRadius: 26,
    width: "100%",
    maxWidth: 790,
    maxHeight: "94%",
    overflow: "hidden",
    borderWidth: 1,
    borderColor: colors.line,
  },
});
/** Native driver where it exists; react-native-web animates in JS. */
const nativeDriver = Platform.OS !== "web";

/** True when the OS asks for less motion; animations then jump straight to their end state. */
export function useReducedMotion() {
  const [reduced, setReduced] = useState(false);
  useEffect(() => {
    let live = true;
    AccessibilityInfo.isReduceMotionEnabled?.().then((v) => live && setReduced(v), () => {});
    const sub = AccessibilityInfo.addEventListener?.("reduceMotionChanged", setReduced);
    return () => {
      live = false;
      sub?.remove?.();
    };
  }, []);
  return reduced;
}

/** Spring scale for a pressable: dips on press-in, springs back with a little overshoot. */
export function usePressSpring(depth = 0.95) {
  const reduced = useReducedMotion();
  const scale = useRef(new Animated.Value(1)).current;
  const to = (value: number, bounciness: number) => {
    if (reduced) scale.setValue(1);
    else Animated.spring(scale, { toValue: value, speed: 40, bounciness, useNativeDriver: nativeDriver }).start();
  };
  return {
    scale,
    onPressIn: () => to(depth, 0),
    onPressOut: () => to(1, 12),
  };
}

export type ButtonVariant = "primary" | "secondary" | "ghost" | "danger" | "icon";

const variantStyles: Record<ButtonVariant, { bg: string; hover: string; fg: string }> = {
  primary: { bg: colors.blue, hover: "#B4DCFB", fg: colors.text },
  secondary: { bg: "#F1F2F3", hover: "#E6E8EA", fg: colors.text },
  ghost: { bg: "transparent", hover: colors.line, fg: colors.text },
  danger: { bg: "#FBEFED", hover: "#F6DEDA", fg: colors.danger },
  icon: { bg: "transparent", hover: colors.line, fg: colors.muted },
};

/**
 * Pressable button with a spring press (scale dips, then springs back) and hover tint on web.
 * `primary` / `danger` keep working; `variant` picks primary | secondary | ghost | danger | icon.
 */
export function Button({
  children,
  onPress,
  icon: Icon,
  primary,
  disabled,
  busy,
  small,
  danger,
  variant,
  accessibilityLabel,
  color,
  style,
}: {
  children?: ReactNode;
  onPress: () => void;
  icon?: LucideIcon;
  primary?: boolean;
  disabled?: boolean;
  busy?: boolean;
  small?: boolean;
  danger?: boolean;
  variant?: ButtonVariant;
  accessibilityLabel?: string;
  /** Overrides the variant's text / icon color. */
  color?: string;
  style?: ViewStyle;
}) {
  const kind: ButtonVariant = variant ?? (danger ? "danger" : primary ? "primary" : "secondary");
  const v = color ? { ...variantStyles[kind], fg: color } : variantStyles[kind];
  const press = usePressSpring(kind === "icon" ? 0.86 : 0.96);
  const [hovered, setHovered] = useState(false);
  const off = !!(disabled || busy);
  const iconOnly = kind === "icon" || children == null;
  const size = small ? 30 : 36;
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={accessibilityLabel}
      disabled={off}
      accessibilityState={{ disabled: off, busy: !!busy }}
      onPress={onPress}
      onPressIn={press.onPressIn}
      onPressOut={press.onPressOut}
      onHoverIn={() => setHovered(true)}
      onHoverOut={() => setHovered(false)}
      style={({ pressed }) => [
        s.button,
        { backgroundColor: (pressed || hovered) && !off ? v.hover : v.bg },
        small && { minHeight: 38, paddingVertical: 7, paddingHorizontal: 13 },
        iconOnly && {
          width: size, height: size, minHeight: size, paddingHorizontal: 0, paddingVertical: 0, borderRadius: size / 2,
        },
        off && { opacity: 0.5 },
        style,
      ]}
    >
      <Animated.View
        style={{ flexDirection: "row", alignItems: "center", gap: 8, transform: [{ scale: press.scale }] }}
      >
        {busy ? (
          <ActivityIndicator color={v.fg} size="small" />
        ) : Icon ? (
          <Icon size={iconOnly ? (small ? 15 : 17) : 15} color={v.fg} strokeWidth={iconOnly ? 1.8 : 2} />
        ) : null}
        {children != null && kind !== "icon" && <Text style={[s.buttonText, { color: v.fg }]}>{children}</Text>}
      </Animated.View>
    </Pressable>
  );
}

/**
 * Checkbox whose box fills with one spring while the tick wipes in left to right and the box
 * pops (after reactbits "spring check"). With reduced motion it switches instantly.
 */
export function SpringCheck({ checked, size = 19 }: { checked: boolean; size?: number }) {
  const reduced = useReducedMotion();
  const progress = useRef(new Animated.Value(checked ? 1 : 0)).current;
  const pop = useRef(new Animated.Value(1)).current;
  const first = useRef(true);
  useEffect(() => {
    if (first.current) {
      first.current = false;
      return;
    }
    if (reduced) {
      progress.setValue(checked ? 1 : 0);
      return;
    }
    Animated.parallel([
      Animated.spring(progress, { toValue: checked ? 1 : 0, speed: 16, bounciness: 4, useNativeDriver: false }),
      Animated.sequence([
        Animated.timing(pop, { toValue: 0.8, duration: 70, useNativeDriver: false }),
        Animated.spring(pop, { toValue: 1, speed: 20, bounciness: 16, useNativeDriver: false }),
      ]),
    ]).start();
  }, [checked, reduced, progress, pop]);
  const tick = size - 6;
  const fill = progress.interpolate({ inputRange: [0, 1], outputRange: ["#FFFFFF", colors.text] });
  const border = progress.interpolate({ inputRange: [0, 1], outputRange: ["#C9CDD1", colors.text] });
  const tickWidth = progress.interpolate({ inputRange: [0, 0.3, 1], outputRange: [0, 0, tick], extrapolate: "clamp" });
  return (
    <Animated.View
      style={{
        width: size,
        height: size,
        borderRadius: 5,
        borderWidth: 1.5,
        borderColor: border,
        backgroundColor: fill,
        justifyContent: "center",
        alignItems: "flex-start",
        paddingLeft: (size - 3 - tick) / 2,
        transform: [{ scale: pop }],
      }}
    >
      <Animated.View style={{ width: tickWidth, height: tick, overflow: "hidden" }}>
        <Check size={tick} color="#FFF" strokeWidth={3} />
      </Animated.View>
    </Animated.View>
  );
}

/** Copy text to the clipboard where the platform allows it (web / WebView2). */
export async function copyText(text: string) {
  const clip = typeof navigator !== "undefined" ? navigator.clipboard : undefined;
  if (!clip?.writeText) return false;
  try {
    await clip.writeText(text);
    return true;
  } catch {
    return false;
  }
}

/** Icon button that copies `text`, then shows a check (or a cross if the copy failed) briefly. */
export function CopyButton({ text, label, copiedLabel, failedLabel, withText, onDark }: {
  text: string; label: string; copiedLabel: string; failedLabel: string; withText?: boolean;
  /** Light text for dark surfaces such as code blocks. */
  onDark?: boolean;
}) {
  const [state, setState] = useState<"idle" | "copied" | "failed">("idle");
  useEffect(() => {
    if (state === "idle") return;
    const id = setTimeout(() => setState("idle"), 1600);
    return () => clearTimeout(id);
  }, [state]);
  const current = state === "copied" ? copiedLabel : state === "failed" ? failedLabel : label;
  return (
    <Button
      variant={withText ? "ghost" : "icon"}
      small
      icon={state === "copied" ? Check : state === "failed" ? X : Copy}
      color={onDark ? "#C9D3D8" : undefined}
      accessibilityLabel={current}
      style={withText
        ? { minHeight: 26, paddingVertical: 3, paddingHorizontal: 8, ...(onDark ? { backgroundColor: "transparent" } : null) }
        : undefined}
      onPress={() => void copyText(text).then((ok) => setState(ok ? "copied" : "failed"))}
    >
      {withText ? <Text style={{ fontSize: 11, color: onDark ? "#C9D3D8" : colors.muted }}>{current}</Text> : undefined}
    </Button>
  );
}
export function IconButton({
  icon: Icon,
  label,
  onPress,
}: {
  icon: LucideIcon;
  label: string;
  onPress: () => void;
}) {
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={label}
      onPress={onPress}
      style={({ pressed }) => [
        {
          width: 44,
          height: 44,
          alignItems: "center",
          justifyContent: "center",
          borderRadius: 22,
          backgroundColor: pressed ? colors.line : "#FFFFFF",
          cursor: "pointer",
        },
      ]}
    >
      <Icon size={20} strokeWidth={1.8} color={colors.text} />
    </Pressable>
  );
}
export function Card({ children, style }: { children: ReactNode; style?: ViewStyle }) {
  return <View style={[s.card, style]}>{children}</View>;
}
export function Chip({ children, tint }: { children: ReactNode; tint?: string }) {
  return (
    <View style={[s.chip, tint ? { backgroundColor: tint } : null]}>
      <Text style={s.chipText}>{children}</Text>
    </View>
  );
}
export function Field({ label, ...props }: TextInputProps & { label: string }) {
  return (
    <View style={s.field}>
      <Text style={[s.small, { fontWeight: "600", color: colors.text }]}>{label}</Text>
      <TextInput
        placeholderTextColor={colors.muted}
        accessibilityLabel={label}
        {...props}
        style={[
          s.input,
          props.multiline && { minHeight: 120, textAlignVertical: "top" },
          props.style,
        ]}
      />
    </View>
  );
}
export function Empty({
  icon: Icon,
  title,
  detail,
  children,
}: {
  icon: LucideIcon;
  title: string;
  detail: string;
  children?: ReactNode;
}) {
  return (
    <View style={{ alignItems: "center", padding: 40, gap: 13 }}>
      <View style={[s.iconBox, { width: 55, height: 55, borderRadius: 18 }]}>
        <Icon size={24} color={colors.blueDark} />
      </View>
      <Text style={s.heading}>{title}</Text>
      <Text style={[s.muted, { textAlign: "center", maxWidth: 360 }]}>{detail}</Text>
      {children}
    </View>
  );
}
export function ErrorNotice({ error }: { error?: string }) {
  return error ? (
    <View accessibilityRole="alert" style={s.error}>
      <Text style={[s.text, { color: colors.danger }]}>{error}</Text>
    </View>
  ) : null;
}
export function Sheet({
  title,
  subtitle,
  children,
  onClose,
  wide,
}: {
  title: string;
  subtitle?: string;
  children: ReactNode;
  onClose: () => void;
  wide?: boolean;
}) {
  const { width } = useWindowDimensions();
  const insets = useSafeAreaInsets();
  const compact = width < 600;
  return (
    <Modal transparent animationType={compact ? "slide" : "fade"} visible onRequestClose={onClose}>
      <View style={[s.modalShade, compact && { padding: 0, justifyContent: "flex-end" }]}>
        <View
          accessibilityViewIsModal
          style={[
            s.sheet,
            wide && { maxWidth: 1050 },
            compact && {
              borderBottomLeftRadius: 0,
              borderBottomRightRadius: 0,
              paddingBottom: Math.max(insets.bottom, 12),
              maxHeight: "94%",
            },
          ]}
        >
          {compact && (
            <View
              style={{
                alignSelf: "center",
                width: 34,
                height: 4,
                borderRadius: 3,
                backgroundColor: "#D8DBDE",
                marginTop: 10,
              }}
            />
          )}
          <View
            style={[
              s.between,
              { padding: compact ? 20 : 24, borderBottomWidth: 1, borderBottomColor: colors.line },
            ]}
          >
            <View style={{ flex: 1, gap: 4 }}>
              <Text style={s.title}>{title}</Text>
              {!!subtitle && <Text style={s.muted}>{subtitle}</Text>}
            </View>
            <IconButton icon={X} label="Close details" onPress={onClose} />
          </View>
          <ScrollView
            keyboardShouldPersistTaps="handled"
            contentContainerStyle={{ padding: compact ? 20 : 24 }}
          >
            {children}
          </ScrollView>
        </View>
      </View>
    </Modal>
  );
}
export function CheckRow({
  label,
  checked,
  onPress,
}: {
  label: string;
  checked: boolean;
  onPress: () => void;
}) {
  const press = usePressSpring(0.98);
  return (
    <Pressable
      accessibilityRole="checkbox"
      accessibilityState={{ checked }}
      accessibilityLabel={label}
      onPress={onPress}
      onPressIn={press.onPressIn}
      onPressOut={press.onPressOut}
    >
      <Animated.View style={[s.row, { gap: 10, paddingVertical: 9, transform: [{ scale: press.scale }] }]}>
        <SpringCheck checked={checked} />
        <Text style={[s.text, { flex: 1 }]}>{label}</Text>
      </Animated.View>
    </Pressable>
  );
}
export function SectionHeading({
  title,
  action,
  onPress,
}: {
  title: string;
  action?: string;
  onPress?: () => void;
}) {
  return (
    <View style={[s.between, { marginBottom: 19 }]}>
      <Text style={s.heading}>{title}</Text>
      {action && onPress && (
        <Pressable accessibilityRole="button" onPress={onPress} style={[s.row, { gap: 5 }]}>
          <Text style={[s.small, { color: colors.text }]}>{action}</Text>
          <ArrowUpRight size={13} color={colors.muted} />
        </Pressable>
      )}
    </View>
  );
}
export function LinkRow({
  title,
  detail,
  onPress,
  icon: Icon,
  tint,
}: {
  title: string;
  detail?: string;
  onPress: () => void;
  icon: LucideIcon;
  tint?: string;
}) {
  return (
    <Pressable
      accessibilityRole="button"
      onPress={onPress}
      style={({ pressed }) => [
        s.row,
        { paddingVertical: 13, gap: 14, borderRadius: 10 },
        pressed && { backgroundColor: colors.canvas },
      ]}
    >
      <View style={[s.iconBox, { backgroundColor: tint || colors.sky }]}>
        <Icon size={19} color={colors.text} />
      </View>
      <View style={{ flex: 1, gap: 3 }}>
        <Text style={[s.text, { fontWeight: "500" }]}>{title}</Text>
        {!!detail && <Text style={s.small}>{detail}</Text>}
      </View>
      <ChevronRight size={15} color={colors.muted} />
    </Pressable>
  );
}
/** Original assistant mascot. */
export function Mascot({
  size = 42,
  variant = "sky",
}: {
  size?: number;
  variant?: "sky" | "sand" | "lilac";
}) {
  const palette = {
    sky: "#ECF5FA",
    sand: "#FAF0DF",
    lilac: "#F1ECF9",
  }[variant];
  return (
    <View accessibilityLabel="Agent mascot" style={{ width: size, height: size }}>
      <View
        style={{
          position: "absolute",
          top: size * 0.15,
          left: size * 0.12,
          width: size * 0.76,
          height: size * 0.76,
          borderRadius: size,
          backgroundColor: palette,
        }}
      />
      <Image
        source={require("../assets/capybara.png")}
        resizeMode="contain"
        style={{ width: size, height: size }}
        accessible={false}
      />
    </View>
  );
}
export function dateLabel(value: string, options?: Intl.DateTimeFormatOptions) {
  const date = new Date(value);
  return Number.isNaN(date.getTime())
    ? value
    : date.toLocaleDateString("en-US", options || { month: "short", day: "numeric" });
}
export function timeLabel(value: string, timeZone?: string) {
  const date = new Date(value);
  return Number.isNaN(date.getTime())
    ? value
    : date.toLocaleTimeString("en-US", { hour: "numeric", minute: "2-digit", timeZone });
}
export function relativeDate(value: string) {
  const diff = Date.now() - new Date(value).getTime();
  return diff < 60_000
    ? "Just now"
    : diff < 3600_000
      ? `${Math.floor(diff / 60_000)}m ago`
      : diff < 86400_000
        ? `${Math.floor(diff / 3600_000)}h ago`
        : dateLabel(value);
}

export function resultSummary(value: string) {
  return /^Saved to (?:sample|local) sent mail(?: · .+)?$/.test(value)
    ? "Reply saved in your local Sent mail."
    : value;
}
