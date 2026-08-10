import { defineConfig } from "vite"
import react from "@vitejs/plugin-react"
import basicSsl  from "@vitejs/plugin-basic-ssl"
import tailwindcss from "@tailwindcss/vite"
import { VitePWA } from "vite-plugin-pwa"
//import {updateClients} from "@microsoft/kiota"

export default defineConfig({
	plugins: [
		basicSsl(),
		react(),
		tailwindcss(),
		VitePWA({
			manifest: {
				background_color: "#cdc5b9",
				display: "minimal-ui",
				icons: [
					{
						sizes: "1024x1024",
						src: "/favicon.png",
						type: "image/png"
					}
				],
				name: "Scarlet Radio Control",
				short_name: "Scarlet Radio Control",
				start_url: "/",
				theme_color: "#ff2400"
			},
			registerType: "autoUpdate",
			workbox: {
				navigateFallbackDenylist: [
					/^\/api/,
					/^\/hubs/
				]
			}
		}),
		/*
		{
			buildStart: ()=>{
				updateClients({
					cleanOutput: true,
					clearCache: true,
					workspacePath: "./src/kiota/output/"
				});
			},
			name: "kiota"
		}
		*/
	],
	server: {
		proxy: {
			"/api": {
				changeOrigin: true,
				secure: false,
				target: "https://localhost:7001",
				ws: true,
			},
			"/hubs": {
				changeOrigin: true,
				secure: false,
				target: "https://localhost:7001",
				ws: true,
			}
		}
	}
})
