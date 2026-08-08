import { useEffect, type DependencyList } from "react";

type EffectAsyncCleanup = () => void;

type EffectAsyncCallback = (abortSignal: AbortSignal) => void | EffectAsyncCleanup | Promise<void | EffectAsyncCleanup>;

export default function useEffectAsync(effectAsyncCallback: EffectAsyncCallback, deps?: DependencyList): void {
	useEffect(() => {
		const abortController = new AbortController();
		let cleanup: EffectAsyncCleanup | void;

		const promise = Promise.resolve(effectAsyncCallback(abortController.signal))
			.then((result) => {
				cleanup = result;
			})
			.catch((reason) => {
				if (!abortController.signal.aborted) {
					console.error(reason);
				}
			});

		return () => {
			abortController.abort();
			promise.finally(() => {
				cleanup?.();
			});
		};
		// eslint-disable-next-line react-hooks/exhaustive-deps
	}, deps);
}
