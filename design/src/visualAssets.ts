/// <reference types="vite/client" />
import {
  siDiscord, siGithub, siGoogle, siReddit, siRoblox, siSpotify,
  siSteam, siTelegram, siTiktok, siTwitch, siVk, siYoutube
} from "simple-icons";

const brandIcons = {
  discord: siDiscord, youtube: siYoutube, telegram: siTelegram,
  steam: siSteam, spotify: siSpotify, github: siGithub,
  google: siGoogle, vk: siVk, reddit: siReddit,
  tiktok: siTiktok, roblox: siRoblox, twitch: siTwitch
} as const;

export function brandIcon(id?: string): { path: string; color: string } | null {
  if (!id || !(id in brandIcons)) return null;
  const icon = brandIcons[id as keyof typeof brandIcons];
  return { path: icon.path, color: `#${icon.hex}` };
}

const flagSvgs = import.meta.glob("../node_modules/flag-icons/flags/4x3/*.svg", {
  query: "?url", import: "default", eager: true
}) as Record<string, string>;

export function flagImage(code?: string): string | null {
  const country = code?.trim().toLowerCase();
  if (!country || !/^[a-z]{2}$/.test(country)) return null;
  return flagSvgs[`../node_modules/flag-icons/flags/4x3/${country}.svg`] ?? null;
}
