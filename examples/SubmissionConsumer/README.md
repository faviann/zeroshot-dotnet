# Native submission package consumer

This standalone consumer references the packed `Zeroshot.Client` package. It is run
by [the stock-native harness](../../tools/native-witness/README.md), which supplies
an existing origin, complete generated asset directory and evidence directory.
It proves full asset admission, contained-provider normalization, normalized
deduplication, exact retained replay, identity mismatch and native refusals using
the public typed and retained submission overloads. It does not launch native or
claim successful provider/forge execution.
