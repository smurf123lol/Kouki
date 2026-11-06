#!/usr/bin/env python3
"""
train_intent_bert.py
Обучение RuBERT-tiny2 классификатора интентов для Kouki и экспорт в ONNX.

Датасет НЕ генерируется внутри скрипта: он собирается из файлов фраз команд и
журнала разбора командой

    dotnet run --project tools/Kouki.IntentDataset -- --out datasets/intents/dataset.jsonl

Скрипт читает:
  1. datasets/intents/dataset.jsonl — строки {"text", "label", "source", "weight"};
  2. datasets/intents/classes.json — единственный список классов модели (порядок = id).

Вес записи из датасета эмулируется повтором примера в выборке. Аугментация
(префиксы/суффиксы) остаётся отдельным флагом --augment/--no-augment и по
умолчанию включена. Модель учится на cointegrated/rubert-tiny2, проверяет
F1 ≥ 0.95 и экспортирует ONNX с динамическими осями, vocab.txt и intents.json.
"""

import io
import os
import sys
import json
import random
import argparse
import numpy as np

import torch
from torch.utils.data import Dataset, DataLoader
from torch.optim import AdamW
from transformers import AutoTokenizer, AutoModelForSequenceClassification
from sklearn.model_selection import train_test_split
from sklearn.metrics import classification_report, f1_score

# Пути по умолчанию: датасет и классы собираются сборщиком tools/Kouki.IntentDataset.
DEFAULT_DATASET_FILE = "datasets/intents/dataset.jsonl"
DEFAULT_CLASSES_FILE = "datasets/intents/classes.json"
DEFAULT_OUTPUT_DIR = "models/intent"

# Вспомогательные префиксы и суффиксы для аугментации
PREFIXES = ["", "слушай, ", "коуки, ", "алиса, ", "эй, ", "пожалуйста, ", "скажи, "]
SUFFIXES = ["", ".", "!", "?", " пожалуйста", " срочно"]

# Минимальное качество, при котором модель можно выкладывать.
MIN_F1 = 0.95


class DataError(Exception):
    """Входные данные непригодны: нет датасета, битая строка, неизвестная метка."""


def load_classes(path):
    """Читает единый список классов модели: порядок в файле задаёт id класса."""
    if not os.path.exists(path):
        raise DataError(f"Файл классов '{path}' не найден.")
    try:
        with io.open(path, encoding="utf-8") as handle:
            payload = json.load(handle)
    except (OSError, json.JSONDecodeError) as error:
        raise DataError(f"Файл классов '{path}' не разбирается: {error}") from error

    classes = payload.get("classes") if isinstance(payload, dict) else None
    if not isinstance(classes, list) or not classes:
        raise DataError(f"Файл классов '{path}' пуст.")
    if any(not isinstance(name, str) or not name.strip() for name in classes):
        raise DataError(f"Файл классов '{path}': пустое имя класса.")
    if len(set(classes)) != len(classes):
        raise DataError(f"Файл классов '{path}': имена классов повторяются.")
    return classes


def load_dataset(path, classes):
    """Читает JSONL-датасет и проверяет, что все метки входят в классы модели."""
    if not os.path.exists(path):
        raise DataError(
            f"Датасет '{path}' не найден. Соберите его: "
            f"dotnet run --project tools/Kouki.IntentDataset -- --out {path}"
        )

    known = set(classes)
    records = []
    with io.open(path, encoding="utf-8") as handle:
        for number, line in enumerate(handle, start=1):
            line = line.strip()
            if not line:
                continue
            try:
                row = json.loads(line)
            except json.JSONDecodeError as error:
                raise DataError(f"Датасет '{path}': строка {number} не разбирается: {error}") from error

            text = row.get("text")
            label = row.get("label")
            if not isinstance(text, str) or not text.strip():
                raise DataError(f"Датасет '{path}': строка {number}: пустой текст.")
            if label not in known:
                raise DataError(f"Метка '{label}' из датасета не входит в классы модели.")
            weight = row.get("weight", 1)
            if not isinstance(weight, (int, float)) or weight <= 0:
                raise DataError(f"Датасет '{path}': строка {number}: вес должен быть положительным числом.")
            records.append((text.strip(), label, float(weight), row.get("source", "command")))

    if not records:
        raise DataError(f"Датасет '{path}' пуст.")
    return records


def generate_training_dataset(records, classes, augment=True):
    """Разворачивает датасет в обучающие примеры: вес = повтор, аугментация — для команд."""
    intents = {name: index for index, name in enumerate(classes)}
    texts = []
    labels = []

    for text, label, weight, source in records:
        intent_id = intents[label]
        repeats = max(1, int(round(weight)))
        for _ in range(repeats):
            texts.append(text)
            labels.append(intent_id)

            # Аугментация только для фраз команд: журнальные фразы — как их сказал человек.
            if not augment or source != "command":
                continue
            for prefix in PREFIXES:
                if not prefix:
                    continue
                texts.append(prefix + text)
                labels.append(intent_id)
            for suffix in SUFFIXES:
                if not suffix or text.endswith(suffix):
                    continue
                texts.append(text + suffix)
                labels.append(intent_id)

    # Перемешиваем детерминированно, чтобы прогоны совпадали.
    order = list(range(len(texts)))
    random.seed(42)
    random.shuffle(order)
    texts = [texts[i] for i in order]
    labels = [labels[i] for i in order]

    for name in classes:
        if not any(label == intents[name] for label in labels):
            raise DataError(f"В датасете нет фраз класса '{name}'.")

    return texts, labels

class IntentDataset(Dataset):
    def __init__(self, encodings, labels):
        self.encodings = encodings
        self.labels = labels

    def __getitem__(self, idx):
        item = {key: torch.tensor(val[idx]) for key, val in self.encodings.items()}
        item['labels'] = torch.tensor(self.labels[idx])
        return item

    def __len__(self):
        return len(self.labels)

def train_and_export(output_dir, dataset_path, classes_path, epochs=3, batch_size=32, lr=3e-5, augment=True):
    os.makedirs(output_dir, exist_ok=True)
    model_name = "cointegrated/rubert-tiny2"
    print(f"Загрузка токенизатора и базовой модели: {model_name}...")
    tokenizer = AutoTokenizer.from_pretrained(model_name)
    classes = load_classes(classes_path)
    records = load_dataset(dataset_path, classes)
    print(f"Записей в датасете '{dataset_path}': {len(records)}")

    model = AutoModelForSequenceClassification.from_pretrained(model_name, num_labels=len(classes))

    texts, labels = generate_training_dataset(records, classes, augment=augment)
    print(f"Всего обучающих примеров после взвешивания и аугментации: {len(texts)}")

    train_texts, val_texts, train_labels, val_labels = train_test_split(
        texts, labels, test_size=0.15, random_state=42, stratify=labels
    )

    print(f"Выборки: Train={len(train_texts)}, Val={len(val_texts)}")

    train_encodings = tokenizer(train_texts, truncation=True, padding=True, max_length=64)
    val_encodings = tokenizer(val_texts, truncation=True, padding=True, max_length=64)

    train_dataset = IntentDataset(train_encodings, train_labels)
    val_dataset = IntentDataset(val_encodings, val_labels)

    device = torch.device('cuda' if torch.cuda.is_available() else 'cpu')
    print(f"Обучение на устройстве: {device}")
    model.to(device)
    model.train()

    train_loader = DataLoader(train_dataset, batch_size=batch_size, shuffle=True)
    val_loader = DataLoader(val_dataset, batch_size=batch_size, shuffle=False)

    optimizer = AdamW(model.parameters(), lr=lr)

    for epoch in range(epochs):
        model.train()
        total_loss = 0.0
        for batch in train_loader:
            optimizer.zero_grad()
            input_ids = batch['input_ids'].to(device)
            attention_mask = batch['attention_mask'].to(device)
            token_type_ids = batch.get('token_type_ids')
            if token_type_ids is not None:
                token_type_ids = token_type_ids.to(device)
            labels_tensor = batch['labels'].to(device)

            outputs = model(input_ids, attention_mask=attention_mask, token_type_ids=token_type_ids, labels=labels_tensor)
            loss = outputs.loss
            loss.backward()
            optimizer.step()
            total_loss += loss.item()

        avg_loss = total_loss / len(train_loader)

        # Валидация
        model.eval()
        all_preds = []
        all_targets = []
        with torch.no_grad():
            for batch in val_loader:
                input_ids = batch['input_ids'].to(device)
                attention_mask = batch['attention_mask'].to(device)
                token_type_ids = batch.get('token_type_ids')
                if token_type_ids is not None:
                    token_type_ids = token_type_ids.to(device)
                
                outputs = model(input_ids, attention_mask=attention_mask, token_type_ids=token_type_ids)
                preds = torch.argmax(outputs.logits, dim=-1).cpu().numpy()
                all_preds.extend(preds)
                all_targets.extend(batch['labels'].numpy())

        f1 = f1_score(all_targets, all_preds, average='macro')
        print(f"Эпоха {epoch+1}/{epochs} - Loss: {avg_loss:.4f} - Val Macro F1: {f1:.4f}")

    print("\nОтчет о классификации на валидации:")
    print(classification_report(all_targets, all_preds, target_names=classes, digits=4))

    assert f1 >= MIN_F1, f"Критерий готовности не выполнен: F1={f1:.4f} < {MIN_F1}"

    # Экспорт в ONNX
    print("\nЭкспорт модели в ONNX...")
    model.eval()
    model.to('cpu')

    dummy_text = "запиши задачу купить хлеб"
    dummy_inputs = tokenizer(dummy_text, return_tensors="pt")
    onnx_path = os.path.join(output_dir, "rubert-tiny2-intents.onnx")

    input_names = ["input_ids", "attention_mask", "token_type_ids"]
    output_names = ["logits"]
    dynamic_axes = {
        "input_ids": {0: "batch_size", 1: "sequence_length"},
        "attention_mask": {0: "batch_size", 1: "sequence_length"},
        "token_type_ids": {0: "batch_size", 1: "sequence_length"},
        "logits": {0: "batch_size"}
    }

    dummy_args = (
        dummy_inputs["input_ids"],
        dummy_inputs["attention_mask"],
        dummy_inputs["token_type_ids"]
    )

    torch.onnx.export(
        model,
        dummy_args,
        onnx_path,
        input_names=input_names,
        output_names=output_names,
        dynamic_axes=dynamic_axes,
        opset_version=14,
        do_constant_folding=True,
        dynamo=False
    )
    print(f"ONNX модель успешно сохранена: {onnx_path} ({os.path.getsize(onnx_path) / 1024 / 1024:.2f} МБ)")

    # Сохраняем vocab.txt и метаданные
    tokenizer.save_vocabulary(output_dir)
    print(f"Словарь токенизатора сохранен в: {output_dir}")

    meta = {
        "model_name": "rubert-tiny2-intents",
        "base_model": model_name,
        "classes": classes,
        "intent2id": {name: index for index, name in enumerate(classes)},
        "id2intent": {index: name for index, name in enumerate(classes)},
        "threshold": 0.75,
        "f1_macro": float(f1)
    }
    with open(os.path.join(output_dir, "intents.json"), "w", encoding="utf-8") as f:
        json.dump(meta, f, ensure_ascii=False, indent=2)

    print("Экспорт метаданных завершен успешно.")

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Обучение RuBERT-tiny2 классификатора интентов")
    parser.add_argument("--output-dir", default=DEFAULT_OUTPUT_DIR, help="Каталог сохранения ONNX и словаря")
    parser.add_argument("--dataset", default=DEFAULT_DATASET_FILE, help="JSONL, собранный tools/Kouki.IntentDataset")
    parser.add_argument("--classes-file", default=DEFAULT_CLASSES_FILE, help="Список классов модели")
    parser.add_argument("--augment", action=argparse.BooleanOptionalAction, default=True, help="Аугментация префиксами/суффиксами (по умолчанию включена)")
    parser.add_argument("--epochs", type=int, default=3, help="Количество эпох")
    parser.add_argument("--batch-size", type=int, default=32, help="Размер батча")
    parser.add_argument("--lr", type=float, default=4e-5, help="Learning rate")
    args = parser.parse_args()

    try:
        train_and_export(args.output_dir, args.dataset, args.classes_file, args.epochs, args.batch_size, args.lr, args.augment)
    except DataError as error:
        print(f"Ошибка: {error}", file=sys.stderr)
        sys.exit(2)
